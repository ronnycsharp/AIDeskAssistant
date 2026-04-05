using System.Collections.Concurrent;

namespace AIDeskAssistant.Services;

internal sealed class MenuBarAssistantService : IMenuBarAssistantService
{
    private const int StreamingTextChunkLength = 120;
    private const int StreamingAudioChunkLength = 4096;
    private const int LiveAudioSampleRate = 24_000;

    private readonly AIService _assistant;
    private readonly MenuBarSpeechService _speechService;
    private readonly AIDebugLogger? _debugLogger;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, LiveAudioSession> _liveAudioSessions = new(StringComparer.Ordinal);

    private CancellationTokenSource? _activeTurnCts;

    public MenuBarAssistantService(AIService assistant, MenuBarSpeechService speechService, AIDebugLogger? debugLogger = null)
    {
        _assistant = assistant;
        _speechService = speechService;
        _debugLogger = debugLogger;
    }

    public string CurrentVoice => _speechService.CurrentVoice;

    public string CurrentThinkingLevel => _assistant.CurrentThinkingLevel;

    public IReadOnlyList<string> GetAvailableVoices() => _speechService.GetAvailableVoices();

    public IReadOnlyList<string> GetAvailableThinkingLevels() => _assistant.GetAvailableThinkingLevels();

    public Task<string> SetVoiceAsync(string voiceId, CancellationToken ct = default)
        => _speechService.SetVoiceAsync(voiceId, ct);

    public Task<string> SetThinkingLevelAsync(string thinkingLevel, CancellationToken ct = default)
        => Task.FromResult(_assistant.SetThinkingLevel(thinkingLevel));

    public async Task<RealtimeAssistantTurnResult> SendTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text input is required.", nameof(text));

        await _turnLock.WaitAsync(ct);
        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            AIServiceTextResult response = await _assistant.SendMessageWithUsageAsync(text, ct: turnCts.Token);
            byte[]? audioWavBytes = await _speechService.GenerateSpeechWavAsync(response.Text, turnCts.Token);
            return new RealtimeAssistantTurnResult(response.Text, audioWavBytes, response.Usage);
        }
        finally
        {
            _activeTurnCts = null;
            _turnLock.Release();
        }
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamTextAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        RealtimeAssistantTurnResult result = await SendTextAsync(text, ct);
        await foreach (RealtimeAssistantStreamEvent streamEvent in StreamResultAsync(result, ct))
            yield return streamEvent;
    }

    public async Task<RealtimeAssistantTurnResult> SendWaveAudioAsync(byte[] waveBytes, CancellationToken ct = default)
    {
        if (!WaveAudioUtility.TryExtractPcm16FromWave(waveBytes, LiveAudioSampleRate, out _, out string error))
            throw new InvalidOperationException(error);

        await _turnLock.WaitAsync(ct);
        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;

            string transcript = await _speechService.TranscribeWaveAsync(waveBytes, turnCts.Token);
            _debugLogger?.LogHistoryEntry("speech-input", string.IsNullOrWhiteSpace(transcript) ? "<no-speech>" : transcript);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                const string noSpeechMessage = "Keine Sprache erkannt.";
                byte[]? noSpeechAudio = await _speechService.GenerateSpeechWavAsync(noSpeechMessage, turnCts.Token);
                return new RealtimeAssistantTurnResult(noSpeechMessage, noSpeechAudio);
            }

            AIServiceTextResult response = await _assistant.SendMessageWithUsageAsync(transcript, ct: turnCts.Token);
            byte[]? audioWavBytes = await _speechService.GenerateSpeechWavAsync(response.Text, turnCts.Token);
            return new RealtimeAssistantTurnResult(response.Text, audioWavBytes, response.Usage);
        }
        finally
        {
            _activeTurnCts = null;
            _turnLock.Release();
        }
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamWaveAudioAsync(byte[] waveBytes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        RealtimeAssistantTurnResult result = await SendWaveAudioAsync(waveBytes, ct);
        await foreach (RealtimeAssistantStreamEvent streamEvent in StreamResultAsync(result, ct))
            yield return streamEvent;
    }

    public Task<string> StartLiveAudioInputAsync(CancellationToken ct = default)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        _liveAudioSessions[sessionId] = new LiveAudioSession();
        return Task.FromResult(sessionId);
    }

    public Task AppendLiveAudioChunkAsync(string sessionId, byte[] pcmBytes, CancellationToken ct = default)
    {
        if (!_liveAudioSessions.TryGetValue(sessionId, out LiveAudioSession? session))
            throw new InvalidOperationException("No matching live audio input session is active.");

        lock (session.SyncRoot)
        {
            session.Buffer.Write(pcmBytes, 0, pcmBytes.Length);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> CommitLiveAudioInputAsync(string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_liveAudioSessions.TryRemove(sessionId, out LiveAudioSession? session))
            throw new InvalidOperationException("No matching live audio input session is active.");

        byte[] pcmBytes;
        lock (session.SyncRoot)
        {
            pcmBytes = session.Buffer.ToArray();
        }

        byte[] waveBytes = WaveAudioUtility.CreateWaveFile(pcmBytes, LiveAudioSampleRate);
        await foreach (RealtimeAssistantStreamEvent streamEvent in StreamWaveAudioAsync(waveBytes, ct))
            yield return streamEvent;
    }

    public Task<bool> CancelLiveAudioInputAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(_liveAudioSessions.TryRemove(sessionId, out _));

    public Task<bool> CancelActiveTurnAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? activeTurnCts = _activeTurnCts;
        if (activeTurnCts is null)
            return Task.FromResult(false);

        activeTurnCts.Cancel();
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        _activeTurnCts?.Dispose();
        _turnLock.Dispose();

        foreach ((_, LiveAudioSession session) in _liveAudioSessions)
            session.Buffer.Dispose();

        _liveAudioSessions.Clear();
        return ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamResultAsync(RealtimeAssistantTurnResult result, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (string chunk in SplitText(result.Text))
        {
            ct.ThrowIfCancellationRequested();
            yield return new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.TextDelta, TextDelta: chunk);
        }

        if (MenuBarSpeechService.TryExtractStreamingPcm(result.AudioWavBytes, out byte[] pcmBytes))
        {
            foreach (byte[] chunk in SplitBytes(pcmBytes, StreamingAudioChunkLength))
            {
                ct.ThrowIfCancellationRequested();
                yield return new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.AudioDelta, AudioPcmBytes: chunk);
            }
        }

        yield return new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.Completed, FinalText: result.Text, Usage: result.Usage);
        await Task.CompletedTask;
    }

    private static IEnumerable<string> SplitText(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (int index = 0; index < text.Length; index += StreamingTextChunkLength)
        {
            int length = Math.Min(StreamingTextChunkLength, text.Length - index);
            yield return text.Substring(index, length);
        }
    }

    private static IEnumerable<byte[]> SplitBytes(byte[] bytes, int chunkLength)
    {
        for (int index = 0; index < bytes.Length; index += chunkLength)
        {
            int length = Math.Min(chunkLength, bytes.Length - index);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(bytes, index, chunk, 0, length);
            yield return chunk;
        }
    }

    private static CancellationTokenSource CreateTurnCancellationSource(CancellationToken ct)
        => CancellationTokenSource.CreateLinkedTokenSource(ct);

    private sealed class LiveAudioSession
    {
        public object SyncRoot { get; } = new();
        public MemoryStream Buffer { get; } = new();
    }
}