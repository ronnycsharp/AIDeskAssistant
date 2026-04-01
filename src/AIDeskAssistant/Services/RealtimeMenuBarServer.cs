using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AIDeskAssistant.Services;

internal sealed class RealtimeMenuBarServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly RealtimeAssistantService _assistant;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public RealtimeMenuBarServer(RealtimeAssistantService assistant)
    {
        _assistant = assistant;
    }

    public Uri BaseUri { get; private set; } = null!;

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
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";

            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true }, ct);
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
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateResponse(result), ct);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio")
            {
                using var memoryStream = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(memoryStream, ct);
                RealtimeAssistantTurnResult result = await _assistant.SendWaveAudioAsync(memoryStream.ToArray(), ct);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, CreateResponse(result), ct);
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "Not found." }, ct);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new { error = ex.Message }, ct);
        }
    }

    private static object CreateResponse(RealtimeAssistantTurnResult result) => new
    {
        text = result.Text,
        audioBase64 = result.AudioWavBytes is null ? null : Convert.ToBase64String(result.AudioWavBytes),
        audioMimeType = result.AudioWavBytes is null ? null : "audio/wav",
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload, CancellationToken ct)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
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
    }
}