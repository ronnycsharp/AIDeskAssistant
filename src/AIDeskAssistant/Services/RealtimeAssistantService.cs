using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AIDeskAssistant.Tools;
using OpenAI.Realtime;

namespace AIDeskAssistant.Services;

internal sealed class RealtimeAssistantService : IMenuBarAssistantService
{
    private const string AssistantRole = "assistant";
    private const string InitialLiveStatusMessage = "Ich prüfe jetzt live den aktuellen Zustand und arbeite das Problem Schritt für Schritt ab.";
    private const int MinimumLiveAudioCommitBytes = 4_800;
    private const string LiveAudioTooShortMessage = "Die Live-Audioaufnahme war zu kurz. Bitte etwas länger sprechen.";
    private static readonly string[] BuiltInVoiceIds = new[] { "alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse" };
    private const double ScreenshotHistorySimilarityThreshold = 0.995;

    private readonly string _model;
    private readonly int _sampleRate;
    private readonly DesktopToolExecutor _executor;
    private RealtimeClient _client;
    private readonly AIDebugLogger? _debugLogger;
    private readonly IScreenshotAnalysisService? _screenshotAnalysisService;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private string _apiKey;
    private string _voice;
    private string _thinkingLevel;
    private ScreenshotFingerprint? _latestRetainedScreenshotFingerprint;

    private RealtimeSessionClient? _session;
    private Task? _receiveLoopTask;
    private CancellationTokenSource? _sessionReceiveLoopCts;
    private PendingTurn? _pendingTurn;
    private CancellationTokenSource? _activeTurnCts;
    private LiveAudioInputSession? _liveAudioInputSession;
    private ScreenshotModelAttachment? _latestRetainedScreenshotAttachment;

    public RealtimeAssistantService(string apiKey, DesktopToolExecutor executor, string model, AIDebugLogger? debugLogger = null, IScreenshotAnalysisService? screenshotAnalysisService = null)
    {
        _apiKey = apiKey.Trim();
        _client = new RealtimeClient(_apiKey);
        _executor = executor;
        _model = model;
        _debugLogger = debugLogger;
        _screenshotAnalysisService = screenshotAnalysisService;
        string configuredVoice = Environment.GetEnvironmentVariable("AIDESK_REALTIME_VOICE")
            ?? RealtimeVoicePreferenceStore.TryLoadVoice()
            ?? "alloy";
        _voice = NormalizeVoiceId(configuredVoice);
        _thinkingLevel = ThinkingLevelPreference.Normalize(
            Environment.GetEnvironmentVariable("AIDESK_THINKING_LEVEL")
            ?? RealtimeVoicePreferenceStore.TryLoadThinkingLevel());
        _screenshotAnalysisService?.SetThinkingLevel(_thinkingLevel);
        _sampleRate = TryGetPositiveInt(Environment.GetEnvironmentVariable("AIDESK_REALTIME_SAMPLE_RATE"), 24_000);
        _debugLogger?.LogHistoryEntry("system", AIService.BuildSystemPrompt());
        MenuBarActivityState.Reset("Bereit", "AIDesk ist bereit.");
    }

    public string CurrentVoice => _voice;
    public string CurrentThinkingLevel => _thinkingLevel;
    public string CurrentLanguage => LanguagePreferenceStore.Current;

    public IReadOnlyList<string> GetAvailableVoices()
    {
        string currentVoice = _voice;
        if (BuiltInVoiceIds.Contains(currentVoice, StringComparer.OrdinalIgnoreCase))
            return BuiltInVoiceIds;

        return [currentVoice, .. BuiltInVoiceIds];
    }

    public IReadOnlyList<string> GetAvailableThinkingLevels()
        => ThinkingLevelPreference.GetAvailableLevels();

    public IReadOnlyList<string> GetAvailableLanguages()
        => LanguagePreferenceStore.AvailableLanguages;

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

    public async Task<string> SetThinkingLevelAsync(string thinkingLevel, CancellationToken ct = default)
    {
        string normalizedThinkingLevel = ThinkingLevelPreference.Normalize(thinkingLevel);

        await _sessionLock.WaitAsync(ct);
        try
        {
            _thinkingLevel = normalizedThinkingLevel;
            Environment.SetEnvironmentVariable("AIDESK_THINKING_LEVEL", normalizedThinkingLevel);
            RealtimeVoicePreferenceStore.SaveThinkingLevel(normalizedThinkingLevel);
            _screenshotAnalysisService?.SetThinkingLevel(normalizedThinkingLevel);
            return normalizedThinkingLevel;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task SetLanguageAsync(string language, CancellationToken ct = default)
    {
        string normalizedLanguage = LanguagePreferenceStore.Set(language);

        await _sessionLock.WaitAsync(ct);
        try
        {
            await _turnLock.WaitAsync(ct);
            try
            {
                if (_session is not null)
                    await StopSessionAsync();

                _debugLogger?.LogHistoryEntry("system", $"Language changed to {normalizedLanguage}.");
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

    public async Task SetApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));

        string normalizedApiKey = apiKey.Trim();

        await _sessionLock.WaitAsync(ct);
        try
        {
            await _turnLock.WaitAsync(ct);
            try
            {
                _apiKey = normalizedApiKey;
                _client = new RealtimeClient(normalizedApiKey);
                LanguagePreferenceStore.SaveApiKey(normalizedApiKey);

                if (_session is not null)
                    await StopSessionAsync();

                _debugLogger?.LogHistoryEntry("system", "Realtime API key updated.");
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

        MenuBarActivityState.UpdateStep("Bereite Textanfrage vor", eventMessage: "Textanfrage wird vorbereitet.");
        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            string screenInfo = GetScreenInfoContext();
            string uiContext = GetFrontmostUiContext();
            string preparedUserMessage = AIService.BuildUserMessageWithScreenInfo(text, screenInfo, uiContext);
            _debugLogger?.LogUiContext(uiContext);
            _debugLogger?.LogPreparedUserMessage(preparedUserMessage);
            _debugLogger?.LogHistoryEntry("user", preparedUserMessage);
            MenuBarActivityState.UpdateStep("Textanfrage an Modell gesendet", eventMessage: "Textanfrage wurde an das Modell gesendet.");
            await _session!.AddItemAsync(CreateUserTextMessage(preparedUserMessage), turnCts.Token);
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

    public IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text input is required.", nameof(text));

        return StreamTextCoreAsync(text, ct);
    }

    private async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamTextCoreAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        MenuBarActivityState.UpdateStep("Bereite Textstream vor", eventMessage: "Textstream wird vorbereitet.");
        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            string screenInfo = GetScreenInfoContext();
            string uiContext = GetFrontmostUiContext();
            string preparedUserMessage = AIService.BuildUserMessageWithScreenInfo(text, screenInfo, uiContext);
            _debugLogger?.LogUiContext(uiContext);
            _debugLogger?.LogPreparedUserMessage(preparedUserMessage);
            _debugLogger?.LogHistoryEntry("user", preparedUserMessage);
            PublishInitialLiveStatus(pendingTurn);
            MenuBarActivityState.UpdateStep("Textstream an Modell gesendet", eventMessage: "Textstream wurde an das Modell gesendet.");
            await _session!.AddItemAsync(CreateUserTextMessage(preparedUserMessage), turnCts.Token);
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
            _debugLogger?.LogHistoryEntry("user", $"Wave audio input received: {waveBytes.Length} byte(s)");
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

    public IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamWaveAudioAsync(byte[] waveBytes, CancellationToken ct = default)
    {
        if (!WaveAudioUtility.TryExtractPcm16FromWave(waveBytes, _sampleRate, out byte[] pcmBytes, out string error))
            throw new InvalidOperationException(error);

        return StreamWaveAudioCoreAsync(waveBytes, pcmBytes, ct);
    }

    private async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamWaveAudioCoreAsync(byte[] waveBytes, byte[] pcmBytes, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureSessionAsync(ct);
        await _turnLock.WaitAsync(ct);

        try
        {
            using CancellationTokenSource turnCts = CreateTurnCancellationSource(ct);
            _activeTurnCts = turnCts;
            PendingTurn pendingTurn = BeginTurn();
            _debugLogger?.LogHistoryEntry("user", $"Wave audio input received: {waveBytes.Length} byte(s)");
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
        MenuBarActivityState.UpdateStep("Starte Live-Audio", eventMessage: "Live-Audioaufnahme wird gestartet.");
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
            MenuBarActivityState.UpdateStep("Live-Audio aktiv", eventMessage: "Live-Audioaufnahme ist aktiv.");
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
        MenuBarActivityState.UpdateStep("Sende Live-Audio", eventMessage: "Live-Audio wird an das Modell gesendet.");
        bool rejectedForTooLittleAudio = false;
        await liveAudioInputSession.AudioOperationLock.WaitAsync(liveAudioInputSession.TurnCancellationSource.Token);

        try
        {
            if (liveAudioInputSession.ResponseStarted)
                throw new InvalidOperationException("The live audio input session has already been committed.");

            if (liveAudioInputSession.AppendedAudioBytes < MinimumLiveAudioCommitBytes)
            {
                rejectedForTooLittleAudio = true;
                liveAudioInputSession.ResponseStarted = true;
                try
                {
                    await ClearInputAudioBufferAsync(liveAudioInputSession.TurnCancellationSource.Token);
                }
                catch (InvalidOperationException)
                {
                    // Safe to ignore when the input audio buffer was already empty.
                }

                liveAudioInputSession.PendingTurn.TrySetException(new InvalidOperationException(LiveAudioTooShortMessage));
            }

            if (rejectedForTooLittleAudio)
                goto AfterCommitLock;

            liveAudioInputSession.ResponseStarted = true;
            await _session!.CommitPendingAudioAsync(liveAudioInputSession.TurnCancellationSource.Token);
        }
        finally
        {
            liveAudioInputSession.AudioOperationLock.Release();
        }

AfterCommitLock:

        if (rejectedForTooLittleAudio)
        {
            try
            {
                await foreach (RealtimeAssistantStreamEvent streamEvent in liveAudioInputSession.PendingTurn.ReadEventsAsync(liveAudioInputSession.TurnCancellationSource.Token))
                    yield return streamEvent;
            }
            finally
            {
                CleanupLiveAudioInputSession(sessionId);
            }

            yield break;
        }

        try
        {
            PublishInitialLiveStatus(liveAudioInputSession.PendingTurn);
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

        MenuBarActivityState.UpdateStep("Breche Live-Audio ab", eventMessage: "Live-Audioaufnahme wird abgebrochen.", eventKind: "warning");
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

        MenuBarActivityState.UpdateStep("Initialisiere Realtime-Sitzung", eventMessage: "Realtime-Sitzung wird initialisiert.");
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
            MenuBarActivityState.UpdateStep("Bereit", eventMessage: "Realtime-Sitzung ist bereit.");
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

        _pendingTurn = new PendingTurn(_sampleRate, _debugLogger);
        return _pendingTurn;
    }

    private async Task StartResponseAsync(PendingTurn pendingTurn, CancellationToken ct)
    {
        MenuBarActivityState.UpdateStep("Warte auf Modellantwort", eventMessage: "Antwortgenerierung wurde gestartet.");
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
                // The linked CTS was already disposed during concurrent shutdown.
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

            if (pendingTurn is not null && pendingTurn.IsTerminal)
            {
                _debugLogger?.LogHistoryEntry("assistant", $"Ignored late realtime update after turn completion: {update.GetType().Name}");
                continue;
            }

            switch (update)
            {
                case RealtimeServerUpdateResponseOutputTextDelta textDelta when pendingTurn is not null:
                    MenuBarActivityState.UpdateStep("Empfange Textantwort");
                    pendingTurn.AppendText(textDelta.Delta);
                    pendingTurn.PublishTextDelta(textDelta.Delta);
                    break;

                case RealtimeServerUpdateResponseOutputAudioTranscriptDelta transcriptDelta when pendingTurn is not null:
                    pendingTurn.AppendTranscript(transcriptDelta.Delta);
                    break;

                case RealtimeServerUpdateResponseOutputAudioDelta audioDelta when pendingTurn is not null:
                    MenuBarActivityState.UpdateStep("Empfange Audioantwort");
                    byte[] audioBytes = audioDelta.Delta.ToArray();
                    pendingTurn.AppendAudio(audioBytes);
                    pendingTurn.PublishAudioDelta(audioBytes);
                    break;

                case RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall when pendingTurn is not null:
                    await HandleFunctionCallAsync(functionCall, pendingTurn, ct);
                    break;

                case RealtimeServerUpdateResponseDone responseDone when pendingTurn is not null:
                    MenuBarActivityState.UpdateStep("Antwort abgeschlossen", eventMessage: "Die Modellantwort ist abgeschlossen.");
                    pendingTurn.AddUsage(CreateUsage(responseDone.Response.Usage));
                    if (pendingTurn.DecrementOutstandingResponses() == 0)
                        pendingTurn.TrySetResult();
                    break;

                case RealtimeServerUpdateError errorUpdate when pendingTurn is not null:
                    MenuBarActivityState.UpdateStep("Realtime-Fehler", eventMessage: errorUpdate.Error?.Message ?? "Realtime-Sitzung fehlgeschlagen.", eventKind: "error");
                    pendingTurn.TrySetException(new InvalidOperationException(errorUpdate.Error?.Message ?? "Realtime session error."));
                    break;
            }
        }
    }

    private async Task HandleFunctionCallAsync(RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall, PendingTurn pendingTurn, CancellationToken ct)
    {
        if (pendingTurn.IsTerminal)
        {
            _debugLogger?.LogHistoryEntry(AssistantRole, $"Ignored tool call after turn completion: {functionCall.FunctionName}({functionCall.CallId})");
            return;
        }

        string argumentsJson = functionCall.FunctionArguments.ToString();
        string rawToolResult;
        _debugLogger?.LogToolCall($"→ Tool: {functionCall.FunctionName}({argumentsJson})");
        _debugLogger?.LogHistoryEntry(AssistantRole, $"Requested tool call: {functionCall.FunctionName}({argumentsJson})");
        _debugLogger?.StartToolExecution(functionCall.CallId, functionCall.FunctionName, argumentsJson);

        try
        {
            rawToolResult = _executor.Execute(functionCall.FunctionName, argumentsJson);
            rawToolResult = await EnrichRealtimeToolResultAsync(functionCall.FunctionName, rawToolResult, ct);
            LogRealtimeToolResult(functionCall.CallId, functionCall.FunctionName, rawToolResult);
            _debugLogger?.CompleteToolExecution(functionCall.CallId, rawToolResult);
        }
        catch (Exception ex)
        {
            rawToolResult = DesktopToolExecutor.Err($"Tool '{functionCall.FunctionName}' failed: {ex.Message}");
            _debugLogger?.FailToolExecution(functionCall.CallId, functionCall.FunctionName, argumentsJson, rawToolResult);
        }

        string toolResult = AIService.CompactToolResultForRealtimeTransport(functionCall.FunctionName, rawToolResult);
        _debugLogger?.LogToolResult($"← Result: {rawToolResult}");
        _debugLogger?.LogHistoryEntry("tool", rawToolResult);

        if (pendingTurn.IsTerminal)
        {
            _debugLogger?.LogHistoryEntry(AssistantRole, $"Skipped function-call output for late tool result after turn completion: {functionCall.FunctionName}({functionCall.CallId})");
            return;
        }

        await _session!.AddItemAsync(new RealtimeFunctionCallOutputItem(functionCall.CallId, toolResult), ct);
        await StartResponseAsync(pendingTurn, ct);
    }

    private async Task<string> EnrichRealtimeToolResultAsync(string toolName, string rawToolResult, CancellationToken ct)
    {
        if (_screenshotAnalysisService is null
            || !string.Equals(toolName, "take_screenshot", StringComparison.Ordinal)
            || !AIService.TryParseScreenshotAttachment(rawToolResult, out ScreenshotModelAttachment? attachment)
            || attachment is null)
        {
            return rawToolResult;
        }

        try
        {
            string? analysis = await _screenshotAnalysisService.AnalyzeAsync(attachment, ct);
            if (string.IsNullOrWhiteSpace(analysis))
                return rawToolResult;

            _debugLogger?.LogHistoryEntry("vision", analysis);
            return AIService.AppendScreenshotAnalysis(rawToolResult, analysis);
        }
        catch (Exception ex)
        {
            _debugLogger?.LogHistoryEntry("vision", $"Screenshot analysis failed: {ex.Message}");
            return rawToolResult;
        }
    }

    private void LogRealtimeToolResult(string toolCallId, string toolName, string rawToolResult)
    {
        if (_debugLogger is null)
            return;

        if (!string.Equals(toolName, "take_screenshot", StringComparison.Ordinal)
            || !AIService.TryParseScreenshotAttachment(rawToolResult, out ScreenshotModelAttachment? attachment)
            || attachment is null)
        {
            return;
        }

        ScreenshotFingerprint currentFingerprint = ScreenshotHistoryComparer.CreateFingerprint(attachment.Bytes);
        double? similarity = null;
        bool retainedInHistory = true;

        if (_latestRetainedScreenshotFingerprint is not null)
        {
            similarity = ScreenshotHistoryComparer.CalculateSimilarity(_latestRetainedScreenshotFingerprint, currentFingerprint);
            retainedInHistory = similarity.Value < ScreenshotHistorySimilarityThreshold;
        }

        if (retainedInHistory)
        {
            if (_latestRetainedScreenshotAttachment is not null)
            {
                byte[]? differenceBytes = ScreenshotHistoryComparer.CreateDifferenceVisualization(_latestRetainedScreenshotAttachment.Bytes, attachment.Bytes);
                if (differenceBytes is not null)
                {
                    _debugLogger.LogScreenshotDifference(toolCallId, differenceBytes, "image/png", $"Visual diff against previous retained screenshot. Similarity: {(similarity.HasValue ? similarity.Value.ToString("P2") : "n/a")}");
                }
            }

            _latestRetainedScreenshotAttachment = attachment;
            _latestRetainedScreenshotFingerprint = currentFingerprint;
        }

        _debugLogger.LogScreenshotAttachment(toolCallId, attachment, retainedInHistory, similarity);
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

    private static RealtimeResponseOptions CreateResponseOptions()
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

    private string GetFrontmostUiContext()
    {
        try
        {
            return _executor.Execute("get_frontmost_ui_elements", "{}");
        }
        catch (Exception ex)
        {
            return $"Frontmost UI context unavailable: {ex.Message}";
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

    private void PublishInitialLiveStatus(PendingTurn pendingTurn)
    {
        if (pendingTurn.HasPublishedInitialStatus)
            return;

        pendingTurn.HasPublishedInitialStatus = true;
        pendingTurn.PublishTextDelta(InitialLiveStatusMessage);
        _debugLogger?.LogHistoryEntry(AssistantRole, InitialLiveStatusMessage);
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
        private int _terminalState;

        private readonly AIDebugLogger? _debugLogger;

        public PendingTurn(int sampleRate, AIDebugLogger? debugLogger)
        {
            _sampleRate = sampleRate;
            _debugLogger = debugLogger;
            Completion = new TaskCompletionSource<RealtimeAssistantTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<RealtimeAssistantTurnResult> Completion { get; }

        public bool IsTerminal => Volatile.Read(ref _terminalState) != 0;

        public bool HasPublishedInitialStatus { get; set; }

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
            if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0)
                return;

            string text = _text.Length > 0 ? _text.ToString() : _transcript.ToString();
            byte[]? audio = _audio.Length > 0 ? WaveAudioUtility.CreateWaveFile(_audio.ToArray(), _sampleRate) : null;
            Completion.TrySetResult(new RealtimeAssistantTurnResult(text, audio, _usage));
            _debugLogger?.LogAssistantResponse(text);
            _debugLogger?.LogHistoryEntry(AssistantRole, text);
            _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.Completed, FinalText: text, Usage: _usage));
            _events.Writer.TryComplete();
        }

        public void TrySetException(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0)
                return;

            Completion.TrySetException(exception);
            _debugLogger?.LogHistoryEntry(AssistantRole, $"Error: {exception.Message}");
            _events.Writer.TryWrite(new RealtimeAssistantStreamEvent(RealtimeAssistantStreamEventType.Error, ErrorMessage: exception.Message));
            _events.Writer.TryComplete();
        }

        public void TrySetCanceled()
        {
            if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0)
                return;

            Completion.TrySetCanceled();
            _debugLogger?.LogHistoryEntry(AssistantRole, "Cancelled");
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