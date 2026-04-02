using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AIDeskAssistant.Tools;
using OpenAI.Realtime;

namespace AIDeskAssistant.Services;

internal sealed class RealtimeAssistantService : IAsyncDisposable
{
    private readonly string _model;
    private readonly string _voice;
    private readonly int _sampleRate;
    private readonly DesktopToolExecutor _executor;
    private readonly RealtimeClient _client;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private RealtimeSessionClient? _session;
    private Task? _receiveLoopTask;
    private PendingTurn? _pendingTurn;
    private CancellationTokenSource? _activeTurnCts;
    private LiveAudioInputSession? _liveAudioInputSession;

    public RealtimeAssistantService(string apiKey, DesktopToolExecutor executor, string model)
    {
        _client = new RealtimeClient(apiKey);
        _executor = executor;
        _model = model;
        _voice = Environment.GetEnvironmentVariable("AIDESK_REALTIME_VOICE") ?? "alloy";
        _sampleRate = TryGetPositiveInt(Environment.GetEnvironmentVariable("AIDESK_REALTIME_SAMPLE_RATE"), 24_000);
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
        await _session!.SendInputAudioAsync(BinaryData.FromBytes(pcmBytes), liveAudioInputSession.TurnCancellationSource.Token);
    }

    public async IAsyncEnumerable<RealtimeAssistantStreamEvent> CommitLiveAudioInputAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        LiveAudioInputSession liveAudioInputSession = GetRequiredLiveAudioInputSession(sessionId);
        if (liveAudioInputSession.ResponseStarted)
            throw new InvalidOperationException("The live audio input session has already been committed.");

        liveAudioInputSession.ResponseStarted = true;

        try
        {
            await _session!.CommitPendingAudioAsync(liveAudioInputSession.TurnCancellationSource.Token);
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

        await liveAudioInputSession.TurnCancellationSource.CancelAsync();
        liveAudioInputSession.PendingTurn.TrySetCanceled();

        try
        {
            await ClearInputAudioBufferAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // Safe to ignore when the input audio buffer has already been cleared.
        }

        CleanupLiveAudioInputSession(liveAudioInputSession.SessionId);
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when disposal cancels the receive loop.
            }
        }

        _session?.Dispose();
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

            _session = await _client.StartConversationSessionAsync(_model, new RealtimeSessionClientOptions(), ct);
            await _session.ConfigureConversationSessionAsync(CreateConversationOptions(), ct);
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_disposeCts.Token), _disposeCts.Token);
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
        liveAudioInputSession.TurnCancellationSource.Dispose();
        _turnLock.Release();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        await foreach (RealtimeServerUpdate update in _session!.ReceiveUpdatesAsync(ct))
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

                case RealtimeServerUpdateResponseDone when pendingTurn is not null:
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
                    TurnDetection = null,
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

    private static RealtimeResponseOptions CreateResponseOptions()
    {
        RealtimeResponseOptions options = new();

        options.OutputModalities.Add(new RealtimeOutputModality("audio"));
        return options;
    }

    private static RealtimeMessageItem CreateUserTextMessage(string text)
        => new(new RealtimeMessageRole("user"), [new RealtimeInputTextMessageContentPart(text)]);

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

        public void IncrementOutstandingResponses() => Interlocked.Increment(ref _outstandingResponses);

        public int DecrementOutstandingResponses() => Interlocked.Decrement(ref _outstandingResponses);

        public void TrySetResult()
        {
            string text = _text.Length > 0 ? _text.ToString() : _transcript.ToString();
            byte[]? audio = _audio.Length > 0 ? WaveAudioUtility.CreateWaveFile(_audio.ToArray(), _sampleRate) : null;
            Completion.TrySetResult(new RealtimeAssistantTurnResult(text, audio));
            _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.Completed, FinalText: text));
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
        public bool ResponseStarted { get; set; }
    }
}