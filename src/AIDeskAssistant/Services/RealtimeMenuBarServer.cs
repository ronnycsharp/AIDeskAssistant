using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Specialized;

namespace AIDeskAssistant.Services;

internal sealed class RealtimeMenuBarServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly object DiagnosticsLogSync = new();

    private readonly IMenuBarAssistantService _assistant;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public RealtimeMenuBarServer(IMenuBarAssistantService assistant)
    {
        _assistant = assistant;
    }

    public Uri BaseUri { get; private set; } = null!;

    internal static string DiagnosticsLogFilePath => Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_SERVER_LOG_FILE")
        ?? Path.Combine(Path.GetTempPath(), "AIDeskAssistant", "menu-bar-host.log");

    public Task StartAsync(CancellationToken ct = default)
    {
        int port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.AbsoluteUri);
        _listener.Start();
        _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token), ct);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_listener.IsListening)
            _listener.Stop();

        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
                // Normal during shutdown.
            }
            catch (HttpListenerException)
            {
                // Expected if the listener is stopped while waiting for requests.
            }
        }

        _listener.Close();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";

        try
        {
            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true }, ct);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/voices")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateVoiceSettingsPayload(_assistant.CurrentVoice, _assistant.GetAvailableVoices(), _assistant.CurrentThinkingLevel, _assistant.GetAvailableThinkingLevels()), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/voice")
            {
                VoiceRequest? request = await JsonSerializer.DeserializeAsync<VoiceRequest>(context.Request.InputStream, JsonOptions, ct);
                if (request is null || string.IsNullOrWhiteSpace(request.Voice))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "Voice is required." }, ct);
                    return;
                }

                string currentVoice = await _assistant.SetVoiceAsync(request.Voice, ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateVoiceSettingsPayload(currentVoice, _assistant.GetAvailableVoices(), _assistant.CurrentThinkingLevel, _assistant.GetAvailableThinkingLevels()), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/thinking")
            {
                ThinkingRequest? request = await JsonSerializer.DeserializeAsync<ThinkingRequest>(context.Request.InputStream, JsonOptions, ct);
                if (request is null || string.IsNullOrWhiteSpace(request.ThinkingLevel))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "Thinking level is required." }, ct);
                    return;
                }

                string currentThinkingLevel = await _assistant.SetThinkingLevelAsync(request.ThinkingLevel, ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateVoiceSettingsPayload(_assistant.CurrentVoice, _assistant.GetAvailableVoices(), currentThinkingLevel, _assistant.GetAvailableThinkingLevels()), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/message")
            {
                MessageRequest? request = await JsonSerializer.DeserializeAsync<MessageRequest>(context.Request.InputStream, JsonOptions, ct);
                if (request is null || string.IsNullOrWhiteSpace(request.Text))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "Text is required." }, ct);
                    return;
                }

                RealtimeAssistantTurnResult result = await _assistant.SendTextAsync(request.Text, ct);
                bool includeAudio = request.IncludeAudio ?? ResolveIncludeAudio(context.Request.QueryString, context.Request.Headers);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateResponse(result, includeAudio), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/message-stream")
            {
                MessageRequest? request = await JsonSerializer.DeserializeAsync<MessageRequest>(context.Request.InputStream, JsonOptions, ct);
                if (request is null || string.IsNullOrWhiteSpace(request.Text))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "Text is required." }, ct);
                    return;
                }

                bool includeAudio = request.IncludeAudio ?? ResolveIncludeAudio(context.Request.QueryString, context.Request.Headers);
                await WriteStreamAsync(context.Response, _assistant.StreamTextAsync(request.Text, ct), includeAudio, ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio")
            {
                using var memoryStream = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(memoryStream, ct);
                RealtimeAssistantTurnResult result = await _assistant.SendWaveAudioAsync(memoryStream.ToArray(), ct);
                bool includeAudio = ResolveIncludeAudio(context.Request.QueryString, context.Request.Headers);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateResponse(result, includeAudio), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio-stream")
            {
                using var memoryStream = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(memoryStream, ct);
                bool includeAudio = ResolveIncludeAudio(context.Request.QueryString, context.Request.Headers);
                await WriteStreamAsync(context.Response, _assistant.StreamWaveAudioAsync(memoryStream.ToArray(), ct), includeAudio, ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio-live/start")
            {
                string sessionId = await _assistant.StartLiveAudioInputAsync(ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { sessionId }, ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio-live/chunk")
            {
                string sessionId = GetRequiredSessionId(context.Request.QueryString, context.Request.Headers);
                using var memoryStream = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(memoryStream, ct);
                await _assistant.AppendLiveAudioChunkAsync(sessionId, memoryStream.ToArray(), ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true }, ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio-live/commit-stream")
            {
                string sessionId = GetRequiredSessionId(context.Request.QueryString, context.Request.Headers);
                bool includeAudio = ResolveIncludeAudio(context.Request.QueryString, context.Request.Headers);
                await WriteStreamAsync(context.Response, _assistant.CommitLiveAudioInputAsync(sessionId, ct), includeAudio, ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio-live/cancel")
            {
                string sessionId = GetRequiredSessionId(context.Request.QueryString, context.Request.Headers);
                bool cancelled = await _assistant.CancelLiveAudioInputAsync(sessionId, ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, cancelled }, ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/cancel")
            {
                bool cancelled = await _assistant.CancelActiveTurnAsync(ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, cancelled }, ct);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/wakeword")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateWakeWordPayload(_assistant.WakeWordEnabled, _assistant.CurrentWakeWord), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/wakeword")
            {
                WakeWordRequest? request = await JsonSerializer.DeserializeAsync<WakeWordRequest>(context.Request.InputStream, JsonOptions, ct);
                if (request is null)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "Request body is required." }, ct);
                    return;
                }

                bool enabled = request.Enabled ?? _assistant.WakeWordEnabled;
                string wakeWord = string.IsNullOrWhiteSpace(request.WakeWord) ? _assistant.CurrentWakeWord : request.WakeWord;
                (bool newEnabled, string newWakeWord) = await _assistant.SetWakeWordAsync(enabled, wakeWord, ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateWakeWordPayload(newEnabled, newWakeWord), ct);
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "Not found." }, ct);
        }
        catch (OperationCanceledException)
        {
            if (!context.Response.OutputStream.CanWrite)
                return;

            if (path.EndsWith("-stream", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "cancelled" });
                await context.Response.OutputStream.WriteAsync(bytes, CancellationToken.None);
                await context.Response.OutputStream.WriteAsync("\n"u8.ToArray(), CancellationToken.None);
                context.Response.OutputStream.Close();
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, cancelled = true }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            string diagnosticsLogPath = LogUnhandledException(context.Request.HttpMethod, path, ex);
            await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new
            {
                error = $"{ex.Message} See diagnostics log: {diagnosticsLogPath}",
            }, ct);
        }
    }

    internal static string LogUnhandledException(string? method, string path, Exception ex)
    {
        string diagnosticsLogPath = DiagnosticsLogFilePath;
        string logEntry = BuildUnhandledExceptionLogEntry(method, path, ex);
        string? directory = Path.GetDirectoryName(diagnosticsLogPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        lock (DiagnosticsLogSync)
        {
            File.AppendAllText(diagnosticsLogPath, logEntry + Environment.NewLine, System.Text.Encoding.UTF8);
        }

        return diagnosticsLogPath;
    }

    internal static string BuildUnhandledExceptionLogEntry(string? method, string path, Exception ex)
    {
        List<string> lines =
        [
            $"[{DateTimeOffset.UtcNow:O}] Unhandled menu bar host exception",
            $"Request: {(string.IsNullOrWhiteSpace(method) ? "<unknown>" : method)} {path}",
        ];

        int depth = 0;
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            string prefix = depth == 0 ? "Exception" : $"InnerException[{depth}]";
            lines.Add($"{prefix}: {current.GetType().FullName}: {current.Message}");
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
                lines.Add(current.StackTrace);
            depth++;
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static object CreateResponse(RealtimeAssistantTurnResult result, bool includeAudio) => new
    {
        text = result.Text,
        audioBase64 = includeAudio && result.AudioWavBytes is not null ? Convert.ToBase64String(result.AudioWavBytes) : null,
        audioMimeType = includeAudio && result.AudioWavBytes is not null ? "audio/wav" : null,
        usage = CreateUsagePayload(result.Usage),
    };

    internal static object CreateStreamResponseEvent(RealtimeAssistantStreamEvent streamEvent) => streamEvent.Type switch
    {
        RealtimeAssistantStreamEventType.TextDelta => new
        {
            type = "text_delta",
            text = streamEvent.TextDelta,
        },
        RealtimeAssistantStreamEventType.AudioDelta => new
        {
            type = "audio_delta",
            pcmBase64 = streamEvent.AudioPcmBytes is not null ? Convert.ToBase64String(streamEvent.AudioPcmBytes) : null,
            sampleRate = 24_000,
            audioFormat = "pcm_s16le",
        },
        RealtimeAssistantStreamEventType.Completed => new
        {
            type = "completed",
            text = streamEvent.FinalText,
            usage = CreateUsagePayload(streamEvent.Usage),
        },
        RealtimeAssistantStreamEventType.Error => new
        {
            type = "error",
            error = streamEvent.ErrorMessage,
        },
        _ => new
        {
            type = "error",
            error = "Unknown stream event type."
        },
    };

    private static object? CreateUsagePayload(RealtimeAssistantUsage? usage)
    {
        if (usage is null)
            return null;

        return new
        {
            inputTokens = usage.InputTokens,
            inputTextTokens = usage.InputTextTokens,
            inputAudioTokens = usage.InputAudioTokens,
            inputImageTokens = usage.InputImageTokens,
            cachedInputTokens = usage.CachedInputTokens,
            outputTokens = usage.OutputTokens,
            outputTextTokens = usage.OutputTextTokens,
            outputAudioTokens = usage.OutputAudioTokens,
            totalTokens = usage.TotalTokens,
        };
    }

    internal static object CreateWakeWordPayload(bool enabled, string wakeWord) => new { enabled, wakeWord };

    internal static object CreateVoiceSettingsPayload(string currentVoice, IReadOnlyList<string> availableVoices, string currentThinkingLevel, IReadOnlyList<string> availableThinkingLevels) => new
    {
        currentVoice,
        availableVoices,
        currentThinkingLevel,
        availableThinkingLevels,
    };

    internal static bool ResolveIncludeAudio(NameValueCollection queryString, NameValueCollection headers, bool defaultValue = true)
    {
        string? queryValue = queryString["includeAudio"];
        if (TryParseBoolean(queryValue, out bool includeAudioFromQuery))
            return includeAudioFromQuery;

        string? headerValue = headers["X-AIDesk-Include-Audio"];
        if (TryParseBoolean(headerValue, out bool includeAudioFromHeader))
            return includeAudioFromHeader;

        return defaultValue;
    }

    private static bool TryParseBoolean(string? value, out bool parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim();
        if (normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            parsed = true;
            return true;
        }

        if (normalized.Equals("0", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("false", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            parsed = false;
            return true;
        }

        return false;
    }

    internal static string GetRequiredSessionId(NameValueCollection queryString, NameValueCollection headers)
    {
        string? sessionId = queryString["sessionId"] ?? headers["X-AIDesk-Audio-Session-Id"];
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("sessionId is required.");

        return sessionId.Trim();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload, CancellationToken ct)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
        response.OutputStream.Close();
    }

    private static async Task WriteStreamAsync(HttpListenerResponse response, IAsyncEnumerable<RealtimeAssistantStreamEvent> stream, bool includeAudio, CancellationToken ct)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/x-ndjson; charset=utf-8";
        response.SendChunked = true;

        await foreach (RealtimeAssistantStreamEvent streamEvent in stream.WithCancellation(ct))
        {
            if (!includeAudio && streamEvent.Type == RealtimeAssistantStreamEventType.AudioDelta)
                continue;

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(CreateStreamResponseEvent(streamEvent));
            await response.OutputStream.WriteAsync(bytes, ct);
            await response.OutputStream.WriteAsync("\n"u8.ToArray(), ct);
            await response.OutputStream.FlushAsync(ct);
        }

        response.OutputStream.Close();
    }

    private static int GetFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class MessageRequest
    {
        public string Text { get; set; } = string.Empty;
        public bool? IncludeAudio { get; set; }
    }

    private sealed class VoiceRequest
    {
        public string Voice { get; set; } = string.Empty;
    }

    private sealed class ThinkingRequest
    {
        public string ThinkingLevel { get; set; } = string.Empty;
    }

    private sealed class WakeWordRequest
    {
        public bool? Enabled { get; set; }
        public string? WakeWord { get; set; }
    }
}