using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AIDeskAssistant.Tools;
using OpenAI.Realtime;

namespace AIDeskAssistant.Services;

internal sealed class RealtimeAssistantService : IAsyncDisposable
{
    private static readonly string[] BuiltInVoiceIds = ["alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse"];

    private readonly string _model;
    private readonly int _sampleRate;
    private readonly DesktopToolExecutor _executor;
    private readonly RealtimeClient _client;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private string _voice;

    private RealtimeSessionClient? _session;
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _sessionReceiveLoopCts;
    private PendingTurn? _pendingTurn;
    private CancellationTokenSource? _activeTurnCts;
    private LiveAudioInputSession? _liveAudioInputSession;

    public RealtimeAssistantService(string apiKey, DesktopToolExecutor executor, string model)
    {
        _client = new RealtimeClient(apiKey);
        _executor = executor;
        _model = model;
        string configuredVoice = Environment.GetEnvironmentVariable("AIDESK_REALTIME_VOICE")
            ?? RealtimeVoicePreferenceStore.TryLoadVoice()
            ?? "alloy";
        _voice = NormalizeVoiceId(configuredVoice);
        _sampleRate = TryGetPositiveInt(Environment.GetEnvironmentVariable("AIDESK_REALTIME_SAMPLE_RATE"), 24_000);
    }

    public string CurrentVoice => _voice;

    public IReadOnlyList<string> GetAvailableVoices()
    {
        string currentVoice = _voice;
        if (BuiltInVoiceIds.Contains(currentVoice, StringComparer.OrdinalIgnoreCase))
            return BuiltInVoiceIds;

        return [currentVoice, .. BuiltInVoiceIds];
    }

    public async Task<string> SetVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        string normalizedVoiceId = NormalizeVoiceId(voiceId);

        await _sessionLock.WaitAsync(ct);
        try
        {
            await _turnLock.WaitAsync(ct);
            try
            {
                _voice = normalizedVoiceId;
                Environment.SetEnvironmentVariable("AIDESK_REALTIME_VOICE", normalizedVoiceId);
                RealtimeVoicePreferenceStore.SaveVoice(normalizedVoiceId);

                if (_session is not null)
                    await StopSessionAsync();

                return normalizedVoiceId;
            }
            finally
            {
                _turnLock.Release();
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<RealtimeAssistantTurnResult> SendTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text input is required.", nameof(text));

        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            string screenInfo = GetScreenInfoContext();
            await _session!.AddItemAsync(CreateUserTextMessage(AIService.BuildUserMessageWithScreenInfo(text, screenInfo)), turnCts.Token);
            await StartResponseAsync(pendingTurn, turnCts.Token);
            return await pendingTurn.Completion.Task.WaitAsync(turnCts.Token);
        }
        finally
        {
            _activeTurnCts = null;
            _pendingTurn = null;
            _turnLock.Release();
        }
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamTextAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text input is required.", nameof(text));

        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            string screenInfo = GetScreenInfoContext();
            await _session!.AddItemAsync(CreateUserTextMessage(AIService.BuildUserMessageWithScreenInfo(text, screenInfo)), turnCts.Token);
            await StartResponseAsync(pendingTurn, turnCts.Token);

            await foreach (RealtimeAssistantStreamEvent streamEvent in pendingTurn.ReadEventsAsync(turnCts.Token))
                yield return streamEvent;
        }
        finally
        {
            _activeTurnCts = null;
            _pendingTurn = null;
            _turnLock.Release();
        }
    }

    public async Task<RealtimeAssistantTurnResult> SendWaveAudioAsync(byte[] waveBytes, CancellationToken ct = default)
    {
        if (!WaveAudioUtility.TryExtractPcm16FromWave(waveBytes, _sampleRate, out byte[] pcmBytes, out string error))
            throw new InvalidOperationException(error);

        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            await ClearInputAudioBufferAsync(turnCts.Token);
            await _session!.SendInputAudioAsync(BinaryData.FromBytes(pcmBytes), turnCts.Token);
            await _session!.CommitPendingAudioAsync(turnCts.Token);
            await StartResponseAsync(pendingTurn, turnCts.Token);
            return await pendingTurn.Completion.Task.WaitAsync(turnCts.Token);
        }
        finally
        {
            _activeTurnCts = null;
            _pendingTurn = null;
            _turnLock.Release();
        }
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamWaveAudioAsync(byte[] waveBytes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!WaveAudioUtility.TryExtractPcm16FromWave(waveBytes, _sampleRate, out byte[] pcmBytes, out string error))
            throw new InvalidOperationException(error);

        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            await ClearInputAudioBufferAsync(turnCts.Token);
            await _session!.SendInputAudioAsync(BinaryData.FromBytes(pcmBytes), turnCts.Token);
            await _session!.CommitPendingAudioAsync(turnCts.Token);
            await StartResponseAsync(pendingTurn, turnCts.Token);

            await foreach (RealtimeAssistantStreamEvent streamEvent in pendingTurn.ReadEventsAsync(turnCts.Token))
                yield return streamEvent;
        }
        finally
        {
            _activeTurnCts = null;
            _pendingTurn = null;
            _turnLock.Release();
        }
    }

    public async Task<bool> CancelActiveTurnAsync(CancellationToken ct = default)
    {
        RealtimeSessionClient? session = _session;
        if (session is null)
            return false;

        LiveAudioInputSession? liveAudioInputSession = _liveAudioInputSession;
        if (liveAudioInputSession is not null)
            return await CancelLiveAudioInputAsync(liveAudioInputSession.SessionId, ct);

        CancellationTokenSource? activeTurnCts = _activeTurnCts;
        PendingTurn? pendingTurn = _pendingTurn;
        if (activeTurnCts is null && pendingTurn is null)
            return false;

        if (activeTurnCts is not null)
            await activeTurnCts.CancelAsync();
        pendingTurn?.TrySetCanceled();

        try
        {
            await session.CancelResponseAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // Safe to ignore when there is no response in progress.
        }

        return true;
    }

    public async Task<string> StartLiveAudioInputAsync(CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct);

        LiveAudioInputSession? existingLiveAudioInputSession = _liveAudioInputSession;
        if (existingLiveAudioInputSession is not null)
            await CancelLiveAudioInputSessionAsync(existingLiveAudioInputSession, ct);

        await _turnLock.WaitAsync(ct);

        try
        {
            CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;

            PendingTurn pendingTurn = BeginTurn();
            await ClearInputAudioBufferAsync(turnCts.Token);

            string sessionId = Guid.NewGuid().ToString("N");
            _liveAudioInputSession = new LiveAudioInputSession(sessionId, pendingTurn, turnCts);
            return sessionId;
        }
        catch
        {
            _activeTurnCts = null;
            _pendingTurn = null;
            _turnLock.Release();
            throw;
        }
    }

    public async Task AppendLiveAudioChunkAsync(string sessionId, byte[] pcmBytes, CancellationToken ct = default)
    {
        if (pcmBytes.Length == 0)
            return;

        LiveAudioInputSession liveAudioInputSession = GetRequiredLiveAudioInputSession(sessionId);
        await liveAudioInputSession.AudioOperationLock.WaitAsync(liveAudioInputSession.TurnCancellationSource.Token);

        try
        {
            await _session!.SendInputAudioAsync(BinaryData.FromBytes(pcmBytes), liveAudioInputSession.TurnCancellationSource.Token);
            liveAudioInputSession.AppendedAudioBytes += pcmBytes.Length;
        }
        finally
        {
            liveAudioInputSession.AudioOperationLock.Release();
        }
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> CommitLiveAudioInputAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        LiveAudioInputSession liveAudioInputSession = GetRequiredLiveAudioInputSession(sessionId);
        await liveAudioInputSession.AudioOperationLock.WaitAsync(liveAudioInputSession.TurnCancellationSource.Token);

        try
        {
            if (liveAudioInputSession.ResponseStarted)
                throw new InvalidOperationException("The live audio input session has already been committed.");

            liveAudioInputSession.ResponseStarted = true;
            await _session!.CommitPendingAudioAsync(liveAudioInputSession.TurnCancellationSource.Token);
        }
        finally
        {
            liveAudioInputSession.AudioOperationLock.Release();
        }

        try
        {
            await StartResponseAsync(liveAudioInputSession.PendingTurn, liveAudioInputSession.TurnCancellationSource.Token);

            await foreach (RealtimeAssistantStreamEvent streamEvent in liveAudioInputSession.PendingTurn.ReadEventsAsync(liveAudioInputSession.TurnCancellationSource.Token))
                yield return streamEvent;
        }
        finally
        {
            CleanupLiveAudioInputSession(sessionId);
        }
    }

    public async Task<bool> CancelLiveAudioInputAsync(string sessionId, CancellationToken ct = default)
    {
        LiveAudioInputSession? liveAudioInputSession = _liveAudioInputSession;
        if (liveAudioInputSession is null || !string.Equals(liveAudioInputSession.SessionId, sessionId, StringComparison.Ordinal))
            return false;

        await CancelLiveAudioInputSessionAsync(liveAudioInputSession, ct);
        return true;
    }

    private async Task CancelLiveAudioInputSessionAsync(LiveAudioInputSession liveAudioInputSession, CancellationToken ct)
    {
        await liveAudioInputSession.AudioOperationLock.WaitAsync(ct);

        try
        {
            if (liveAudioInputSession.ResponseStarted)
            {
                try
                {
                    await _session!.CancelResponseAsync(ct);
                }
                catch (InvalidOperationException)
                {
                    // Safe to ignore when the response already finished.
                }
            }

            try
            {
                await ClearInputAudioBufferAsync(ct);
            }
            catch (InvalidOperationException)
            {
                // Safe to ignore when the input audio buffer has already been cleared.
            }
        }
        finally
        {
            liveAudioInputSession.AudioOperationLock.Release();
        }

        await liveAudioInputSession.TurnCancellationSource.CancelAsync();
        liveAudioInputSession.PendingTurn.TrySetCanceled();

        CleanupLiveAudioInputSession(liveAudioInputSession.SessionId);
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();

        await _sessionLock.WaitAsync();
        try
        {
            await StopSessionAsync();
        }
        finally
        {
            _sessionLock.Release();
        }

        _disposeCts.Dispose();
        _sessionLock.Dispose();
        _turnLock.Dispose();
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
            return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_session is not null)
                return;

            RealtimeSessionClient session = await _client.StartConversationSessionAsync(_model, new RealtimeSessionClientOptions(), ct);
            await session.ConfigureConversationSessionAsync(CreateConversationOptions(), ct);

            CancellationTokenSource sessionReceiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _session = session;
            _sessionReceiveLoopCts = sessionReceiveLoopCts;
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(session, sessionReceiveLoopCts.Token), sessionReceiveLoopCts.Token);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private CancellationTokenSource CreateTurnCancellationSource(CancellationToken ct)
        => CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);

    private async Task ClearInputAudioBufferAsync(CancellationToken ct)
    {
        try
        {
            await _session!.ClearInputAudioAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // Safe to ignore when no audio buffer exists yet.
        }
    }

    private PendingTurn BeginTurn()
    {
        if (_pendingTurn is not null)
            throw new InvalidOperationException("Another realtime turn is already in progress.");

        _pendingTurn = new PendingTurn(_sampleRate);
        return _pendingTurn;
    }

    private async Task StartResponseAsync(PendingTurn pendingTurn, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await _session!.StartResponseAsync(CreateResponseOptions(), ct);
                pendingTurn.IncrementOutstandingResponses();
                return;
            }
            catch (InvalidOperationException ex) when (attempt == 0 && IsActiveResponseConflict(ex))
            {
                try
                {
                    await _session!.CancelResponseAsync(ct);
                }
                catch (InvalidOperationException)
                {
                    // If the response already ended between the failure and the cancel call,
                    // just continue to the retry below.
                }

                await Task.Delay(150, ct);
            }
        }
    }

    private static bool IsActiveResponseConflict(InvalidOperationException ex)
        => ex.Message.Contains("active response", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("response in progress", StringComparison.OrdinalIgnoreCase);

    private LiveAudioInputSession GetRequiredLiveAudioInputSession(string sessionId)
    {
        LiveAudioInputSession? liveAudioInputSession = _liveAudioInputSession;
        if (liveAudioInputSession is null || !string.Equals(liveAudioInputSession.SessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("No matching live audio input session is active.");

        return liveAudioInputSession;
    }

    private void CleanupLiveAudioInputSession(string sessionId)
    {
        LiveAudioInputSession? liveAudioInputSession = _liveAudioInputSession;
        if (liveAudioInputSession is null || !string.Equals(liveAudioInputSession.SessionId, sessionId, StringComparison.Ordinal))
            return;

        _liveAudioInputSession = null;
        _activeTurnCts = null;
        _pendingTurn = null;
        liveAudioInputSession.AudioOperationLock.Dispose();
        liveAudioInputSession.TurnCancellationSource.Dispose();
        _turnLock.Release();
    }

    private async Task StopSessionAsync()
    {
        RealtimeSessionClient? session = _session;
        Task? receiveLoopTask = _receiveLoopTask;
        CancellationTokenSource? sessionReceiveLoopCts = _sessionReceiveLoopCts;

        _session = null;
        _receiveLoopTask = null;
        _sessionReceiveLoopCts = null;

        if (sessionReceiveLoopCts is not null)
        {
            try
            {
                await sessionReceiveLoopCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        session?.Dispose();

        if (receiveLoopTask is not null)
        {
            try
            {
                await receiveLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when a session is reset or disposed.
            }
            catch (ObjectDisposedException)
            {
                // Expected when the session is disposed while the receive loop is waiting.
            }
        }

        sessionReceiveLoopCts?.Dispose();
    }

    private async Task ReceiveLoopAsync(RealtimeSessionClient session, CancellationToken ct)
    {
        await foreach (RealtimeServerUpdate update in session.ReceiveUpdatesAsync(ct))
        {
            PendingTurn? pendingTurn = _pendingTurn;

            switch (update)
            {
                case RealtimeServerUpdateResponseOutputTextDelta textDelta when pendingTurn is not null:
                    pendingTurn.AppendText(textDelta.Delta);
                    pendingTurn.PublishTextDelta(textDelta.Delta);
                    break;

                case RealtimeServerUpdateResponseOutputAudioTranscriptDelta transcriptDelta when pendingTurn is not null:
                    pendingTurn.AppendTranscript(transcriptDelta.Delta);
                    break;

                case RealtimeServerUpdateResponseOutputAudioDelta audioDelta when pendingTurn is not null:
                    byte[] audioBytes = audioDelta.Delta.ToArray();
                    pendingTurn.AppendAudio(audioBytes);
                    pendingTurn.PublishAudioDelta(audioBytes);
                    break;

                case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall when pendingTurn is not null:
                    await HandleFunctionCallAsync(functionCall, pendingTurn, ct);
                    break;

                case RealtimeServerUpdateResponseDone responseDone when pendingTurn is not null:
                    pendingTurn.AddUsage(CreateUsage(responseDone.Response.Usage));
                    if (pendingTurn.DecrementOutstandingResponses() == 0)
                        pendingTurn.TrySetResult();
                    break;

                case RealtimeServerUpdateError errorUpdate when pendingTurn is not null:
                    pendingTurn.TrySetException(new InvalidOperationException(errorUpdate.Error?.Message ?? "Realtime session error."));
                    break;
            }
        }
    }

    private async Task HandleFunctionCallAsync(RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall, PendingTurn pendingTurn, CancellationToken ct)
    {
        string argumentsJson = functionCall.FunctionArguments.ToString();
        string toolResult;

        try
        {
            toolResult = _executor.Execute(functionCall.FunctionName, argumentsJson);
            toolResult = AIService.CompactToolResultForRealtimeTransport(functionCall.FunctionName, toolResult);
        }
        catch (Exception ex)
        {
            toolResult = $"Tool '{functionCall.FunctionName}' failed: {ex.Message}";
        }

        await _session!.AddItemAsync(new RealtimeFunctionCallOutputItem(functionCall.CallId, toolResult), ct);
        await StartResponseAsync(pendingTurn, ct);
    }

    private RealtimeConversationSessionOptions CreateConversationOptions()
    {
        RealtimeConversationSessionOptions options = new()
        {
            Instructions = AIService.BuildSystemPrompt(),
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = new RealtimeConversationSessionInputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    TurnDetection = new RealtimeServerVadTurnDetection
                    {
                        SilenceDuration = TimeSpan.FromSeconds(10),
                        CreateResponseEnabled = false,
                        InterruptResponseEnabled = false,
                    },
                },
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    Voice = new RealtimeVoice(_voice),
                },
            },
        };

        options.OutputModalities.Add(new RealtimeOutputModality("audio"));

        foreach (DesktopFunctionToolDefinition definition in DesktopToolDefinitions.FunctionDefinitions)
        {
            RealtimeFunctionTool tool = new(definition.Name)
            {
                FunctionDescription = definition.Description,
                FunctionParameters = definition.Parameters ?? BinaryData.FromString("{\"type\":\"object\",\"properties\":{}}"),
            };

            options.Tools.Add(tool);
        }

        return options;
    }

    private RealtimeResponseOptions CreateResponseOptions()
    {
        RealtimeResponseOptions options = new();

        options.OutputModalities.Add(new RealtimeOutputModality("audio"));
        return options;
    }

    private static RealtimeMessageItem CreateUserTextMessage(string text)
        => new(new RealtimeMessageRole("user"), [new RealtimeInputTextMessageContentPart(text)]);

    private static RealtimeAssistantUsage? CreateUsage(RealtimeResponseUsage? usage)
    {
        if (usage is null)
            return null;

        return new RealtimeAssistantUsage(
            InputTokens: usage.InputTokenCount,
            InputTextTokens: usage.InputTokenDetails?.TextTokenCount,
            InputAudioTokens: usage.InputTokenDetails?.AudioTokenCount,
            InputImageTokens: usage.InputTokenDetails?.ImageTokenCount,
            CachedInputTokens: usage.InputTokenDetails?.CachedTokenCount,
            OutputTokens: usage.OutputTokenCount,
            OutputTextTokens: usage.OutputTokenDetails?.TextTokenCount,
            OutputAudioTokens: usage.OutputTokenDetails?.AudioTokenCount,
            TotalTokens: usage.TotalTokenCount);
    }

    private string GetScreenInfoContext()
    {
        try
        {
            return _executor.Execute("get_screen_info", "{}");
        }
        catch (Exception ex)
        {
            return $"Screen information unavailable: {ex.Message}";
        }
    }

    private static int TryGetPositiveInt(string? value, int fallback)
        => int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;

    private static string NormalizeVoiceId(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required.", nameof(voiceId));

        string trimmedVoiceId = voiceId.Trim();
        string? builtInVoice = BuiltInVoiceIds.FirstOrDefault(candidate => candidate.Equals(trimmedVoiceId, StringComparison.OrdinalIgnoreCase));
        return builtInVoice ?? trimmedVoiceId;
    }

    private sealed class PendingTurn
    {
        private readonly int _sampleRate;
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _transcript = new();
        private readonly MemoryStream _audio = new();
        private readonly Channel<RealtimeAssistantStreamEvent> _events = Channel.CreateUnbounded<RealtimeAssistantStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        private RealtimeAssistantUsage? _usage;
        private int _outstandingResponses;

        public PendingTurn(int sampleRate)
        {
            _sampleRate = sampleRate;
            Completion = new TaskCompletionSource<RealtimeAssistantTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<RealtimeAssistantTurnResult> Completion { get; }

        public IAsyncEnumerable<RealtimeAssistantStreamEvent> ReadEventsAsync(CancellationToken ct)
            => _events.Reader.ReadAllAsync(ct);

        public void AppendText(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                _text.Append(value);
        }

        public void AppendTranscript(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                _transcript.Append(value);
        }

        public void AppendAudio(byte[] bytes)
        {
            if (bytes.Length > 0)
                _audio.Write(bytes, 0, bytes.Length);
        }

        public void PublishTextDelta(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.TextDelta, TextDelta: value));
        }

        public void PublishAudioDelta(byte[] bytes)
        {
            if (bytes.Length > 0)
                _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.AudioDelta, AudioPcmBytes: bytes));
        }

        public void AddUsage(RealtimeAssistantUsage? usage)
        {
            if (usage is null)
                return;

            _usage = _usage is null ? usage : _usage.Add(usage);
        }

        public void IncrementOutstandingResponses() => Interlocked.Increment(ref _outstandingResponses);

        public int DecrementOutstandingResponses() => Interlocked.Decrement(ref _outstandingResponses);

        public void TrySetResult()
        {
            string text = _text.Length > 0 ? _text.ToString() : _transcript.ToString();
            byte[]? audio = _audio.Length > 0 ? WaveAudioUtility.CreateWaveFile(_audio.ToArray(), _sampleRate) : null;
            Completion.TrySetResult(new RealtimeAssistantTurnResult(text, audio, _usage));
            _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.Completed, FinalText: text, Usage: _usage));
            _events.Writer.TryComplete();
        }

        public void TrySetException(Exception exception)
        {
            Completion.TrySetException(exception);
            _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.Error, ErrorMessage: exception.Message));
            _events.Writer.TryComplete();
        }

        public void TrySetCanceled()
        {
            Completion.TrySetCanceled();
            _events.Writer.TryComplete();
        }
    }

    private sealed class LiveAudioInputSession
    {
        public LiveAudioInputSession(string sessionId, PendingTurn pendingTurn, CancellationTokenSource turnCancellationSource)
        {
            SessionId = sessionId;
            PendingTurn = pendingTurn;
            TurnCancellationSource = turnCancellationSource;
        }

        public string SessionId { get; }
        public PendingTurn PendingTurn { get; }
        public CancellationTokenSource TurnCancellationSource { get; }
        public SemaphoreSlim AudioOperationLock { get; } = new(1, 1);
        public int AppendedAudioBytes { get; set; }
        public bool ResponseStarted { get; set; }
    }
}