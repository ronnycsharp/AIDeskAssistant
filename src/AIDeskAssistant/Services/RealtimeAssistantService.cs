using System.Text;
using System.Text.Json;
using AIDeskAssistant.Tools;
using OpenAI.Realtime;

namespace AIDeskAssistant.Services;

internal sealed class RealtimeAssistantService : IAsyncDisposable
{
    private const string SystemPrompt =
        """
        You are an AI desktop assistant that can control the user's computer.
        You have access to tools that let you:
        - Take screenshots to see the current state of the screen
        - Move the mouse and click
        - Type text and press keys
        - Open applications
        - Open URLs directly in the browser
        - Run terminal/CLI commands and read their text output
        - Move and resize the active window
        - Wait between actions

        When the user gives you a task, figure out the necessary steps and execute them one at a time.
        Work like an agent: continue through longer multi-step tasks until the requested outcome is achieved or you are blocked.
        For browser workflows such as Gmail, web shops, or forms, prefer opening the exact URL first and then continue with screenshots, clicks, typing, and waiting as needed.
        For terminal tasks, prefer using terminal output from run_command when you need reliable text results instead of relying only on screenshots.
        On macOS, prefer the Accessibility-based tools for Apple menu items and System Settings sidebar navigation instead of coordinate-based clicks whenever those tools fit the task.
        Always take a screenshot first to understand the current screen state before acting.
        After each significant action, take another screenshot to confirm the result.
        Be precise with coordinates — use the screenshot to determine exact pixel positions.
        If something doesn't work, try an alternative approach.
        Explain what you are doing at each step.
        """;

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
            PendingTurn pendingTurn = BeginTurn();
            await _session!.AddItemAsync(CreateUserTextMessage(text), ct);
            await StartResponseAsync(pendingTurn, ct);
            return await pendingTurn.Completion.Task.WaitAsync(ct);
        }
        finally
        {
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
            PendingTurn pendingTurn = BeginTurn();
            using var stream = new MemoryStream(pcmBytes, writable: false);
            await _session!.SendInputAudioAsync(stream, ct);
            await StartResponseAsync(pendingTurn, ct);
            return await pendingTurn.Completion.Task.WaitAsync(ct);
        }
        finally
        {
            _pendingTurn = null;
            _turnLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch (OperationCanceledException)
            {
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

    private PendingTurn BeginTurn()
    {
        if (_pendingTurn is not null)
            throw new InvalidOperationException("Another realtime turn is already in progress.");

        _pendingTurn = new PendingTurn(_sampleRate);
        return _pendingTurn;
    }

    private async Task StartResponseAsync(PendingTurn pendingTurn, CancellationToken ct)
    {
        pendingTurn.IncrementOutstandingResponses();
        await _session!.StartResponseAsync(CreateResponseOptions(), ct);
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
                    break;

                case RealtimeServerUpdateResponseOutputAudioTranscriptDelta transcriptDelta when pendingTurn is not null:
                    pendingTurn.AppendTranscript(transcriptDelta.Delta);
                    break;

                case RealtimeServerUpdateResponseOutputAudioDelta audioDelta when pendingTurn is not null:
                    pendingTurn.AppendAudio(audioDelta.Delta.ToArray());
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
            Instructions = SystemPrompt,
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

    private RealtimeResponseOptions CreateResponseOptions()
    {
        RealtimeResponseOptions options = new();

        options.OutputModalities.Add(new RealtimeOutputModality("audio"));
        return options;
    }

    private static RealtimeMessageItem CreateUserTextMessage(string text)
        => new(new RealtimeMessageRole("user"), [new RealtimeInputTextMessageContentPart(text)]);

    private static int TryGetPositiveInt(string? value, int fallback)
        => int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;

    private sealed class PendingTurn
    {
        private readonly int _sampleRate;
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _transcript = new();
        private readonly MemoryStream _audio = new();
        private int _outstandingResponses;

        public PendingTurn(int sampleRate)
        {
            _sampleRate = sampleRate;
            Completion = new TaskCompletionSource<RealtimeAssistantTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<RealtimeAssistantTurnResult> Completion { get; }

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

        public void IncrementOutstandingResponses() => Interlocked.Increment(ref _outstandingResponses);

        public int DecrementOutstandingResponses() => Interlocked.Decrement(ref _outstandingResponses);

        public void TrySetResult()
        {
            string text = _text.Length > 0 ? _text.ToString() : _transcript.ToString();
            byte[]? audio = _audio.Length > 0 ? WaveAudioUtility.CreateWaveFile(_audio.ToArray(), _sampleRate) : null;
            Completion.TrySetResult(new RealtimeAssistantTurnResult(text, audio));
        }

        public void TrySetException(Exception exception) => Completion.TrySetException(exception);
    }
}