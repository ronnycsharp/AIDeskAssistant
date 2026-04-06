using System.Collections.Specialized;
using System.Text.Json;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class RealtimeMenuBarServerTests
{
    [Fact]
    public void ResolveIncludeAudio_UsesQueryStringOverride()
    {
        NameValueCollection query = new() { { "includeAudio", "false" } };
        NameValueCollection headers = new() { { "X-AIDesk-Include-Audio", "true" } };

        bool includeAudio = RealtimeMenuBarServer.ResolveIncludeAudio(query, headers);

        Assert.False(includeAudio);
    }

    [Fact]
    public void ResolveIncludeAudio_UsesHeaderWhenQueryMissing()
    {
        NameValueCollection query = new();
        NameValueCollection headers = new() { { "X-AIDesk-Include-Audio", "false" } };

        bool includeAudio = RealtimeMenuBarServer.ResolveIncludeAudio(query, headers);

        Assert.False(includeAudio);
    }

    [Fact]
    public void ResolveIncludeAudio_DefaultsToTrue()
    {
        bool includeAudio = RealtimeMenuBarServer.ResolveIncludeAudio(new NameValueCollection(), new NameValueCollection());

        Assert.True(includeAudio);
    }

    [Fact]
    public void CreateStreamResponseEvent_EncodesAudioDeltaAsBase64Pcm()
    {
        RealtimeAssistantStreamEvent streamEvent = new(
            RealtimeAssistantStreamEventType.AudioDelta,
            AudioPcmBytes: [0x01, 0x02, 0x03, 0x04]);

        object payload = RealtimeMenuBarServer.CreateStreamResponseEvent(streamEvent);
        JsonElement json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal("audio_delta", json.GetProperty("type").GetString());
        Assert.Equal("AQIDBA==", json.GetProperty("pcmBase64").GetString());
        Assert.Equal(24_000, json.GetProperty("sampleRate").GetInt32());
        Assert.Equal("pcm_s16le", json.GetProperty("audioFormat").GetString());
    }

    [Fact]
    public void CreateStreamResponseEvent_EncodesCompletionText()
    {
        RealtimeAssistantStreamEvent streamEvent = new(
            RealtimeAssistantStreamEventType.Completed,
            FinalText: "Bereit.",
            Usage: new RealtimeAssistantUsage(
                InputTokens: 120,
                InputTextTokens: 100,
                CachedInputTokens: 20,
                OutputTokens: 45,
                OutputTextTokens: 40,
                OutputAudioTokens: 5,
                TotalTokens: 165));

        object payload = RealtimeMenuBarServer.CreateStreamResponseEvent(streamEvent);
        JsonElement json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal("completed", json.GetProperty("type").GetString());
        Assert.Equal("Bereit.", json.GetProperty("text").GetString());
        JsonElement usage = json.GetProperty("usage");
        Assert.Equal(120, usage.GetProperty("inputTokens").GetInt32());
        Assert.Equal(100, usage.GetProperty("inputTextTokens").GetInt32());
        Assert.Equal(20, usage.GetProperty("cachedInputTokens").GetInt32());
        Assert.Equal(45, usage.GetProperty("outputTokens").GetInt32());
        Assert.Equal(40, usage.GetProperty("outputTextTokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("outputAudioTokens").GetInt32());
        Assert.Equal(165, usage.GetProperty("totalTokens").GetInt32());
    }

    [Fact]
    public void GetRequiredSessionId_UsesQueryStringFirst()
    {
        NameValueCollection query = new() { { "sessionId", "query-session" } };
        NameValueCollection headers = new() { { "X-AIDesk-Audio-Session-Id", "header-session" } };

        string sessionId = RealtimeMenuBarServer.GetRequiredSessionId(query, headers);

        Assert.Equal("query-session", sessionId);
    }

    [Fact]
    public void GetRequiredSessionId_FallsBackToHeader()
    {
        NameValueCollection query = new();
        NameValueCollection headers = new() { { "X-AIDesk-Audio-Session-Id", "header-session" } };

        string sessionId = RealtimeMenuBarServer.GetRequiredSessionId(query, headers);

        Assert.Equal("header-session", sessionId);
    }

    [Fact]
    public void CreateVoiceSettingsPayload_EncodesCurrentAndAvailableVoices()
    {
        object payload = RealtimeMenuBarServer.CreateVoiceSettingsPayload("marin", ["alloy", "marin", "verse"], "medium", ["default", "low", "medium", "high"]);
        JsonElement json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal("marin", json.GetProperty("currentVoice").GetString());
        string[] availableVoices = json.GetProperty("availableVoices").EnumerateArray().Select(element => element.GetString()).OfType<string>().ToArray();
        Assert.Equal(["alloy", "marin", "verse"], availableVoices);
        Assert.Equal("medium", json.GetProperty("currentThinkingLevel").GetString());
        string[] availableThinkingLevels = json.GetProperty("availableThinkingLevels").EnumerateArray().Select(element => element.GetString()).OfType<string>().ToArray();
        Assert.Equal(["default", "low", "medium", "high"], availableThinkingLevels);
    }

    [Fact]
    public void BuildUnhandledExceptionLogEntry_ContainsRequestAndStackDetails()
    {
        InvalidOperationException exception = new("outer failure", new ArgumentException("inner failure"));

        string logEntry = RealtimeMenuBarServer.BuildUnhandledExceptionLogEntry("POST", "/voice", exception);

        Assert.Contains("Unhandled menu bar host exception", logEntry);
        Assert.Contains("Request: POST /voice", logEntry);
        Assert.Contains("System.InvalidOperationException: outer failure", logEntry);
        Assert.Contains("System.ArgumentException: inner failure", logEntry);
    }

    [Fact]
    public void LogUnhandledException_WritesLogFileAndReturnsPath()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "AIDeskAssistant.Tests", $"menu-bar-host-{Guid.NewGuid():N}.log");
        string? original = Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_SERVER_LOG_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_SERVER_LOG_FILE", tempFile);

            string logPath = RealtimeMenuBarServer.LogUnhandledException("POST", "/message-stream", new InvalidOperationException("boom"));

            Assert.Equal(tempFile, logPath);
            Assert.True(File.Exists(tempFile));
            string content = File.ReadAllText(tempFile);
            Assert.Contains("Request: POST /message-stream", content);
            Assert.Contains("System.InvalidOperationException: boom", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_SERVER_LOG_FILE", original);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}

public sealed class RealtimeMenuBarServerStartRecordingTests : IAsyncDisposable
{
    private readonly RealtimeMenuBarServer _server;
    private readonly HttpClient _client = new();

    public RealtimeMenuBarServerStartRecordingTests()
    {
        _server = new RealtimeMenuBarServer(new StubMenuBarAssistantService());
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _client.Dispose();
    }

    [Fact]
    public async Task PostStartRecording_SetsPendingFlag()
    {
        await _server.StartAsync();

        HttpResponseMessage startResponse = await _client.PostAsync(_server.BaseUri + "start-recording", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, startResponse.StatusCode);

        HttpResponseMessage pollResponse = await _client.GetAsync(_server.BaseUri + "recording-request");
        Assert.Equal(System.Net.HttpStatusCode.OK, pollResponse.StatusCode);
        string json = await pollResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task GetRecordingRequest_ClearsFlagAfterReading()
    {
        await _server.StartAsync();

        await _client.PostAsync(_server.BaseUri + "start-recording", null);

        // First read consumes the flag.
        HttpResponseMessage first = await _client.GetAsync(_server.BaseUri + "recording-request");
        using JsonDocument firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstDoc.RootElement.GetProperty("pending").GetBoolean());

        // Second read sees no pending request.
        HttpResponseMessage second = await _client.GetAsync(_server.BaseUri + "recording-request");
        using JsonDocument secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.False(secondDoc.RootElement.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task GetRecordingRequest_ReturnsFalseWhenNoPendingRequest()
    {
        await _server.StartAsync();

        HttpResponseMessage response = await _client.GetAsync(_server.BaseUri + "recording-request");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task StartAsync_WritesPortFile()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "AIDeskAssistant.Tests", $"port-{Guid.NewGuid():N}.txt");
        string? original = Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_PORT_FILE");
        try
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_PORT_FILE", tempFile);
            await _server.StartAsync();

            Assert.True(File.Exists(tempFile));
            string content = File.ReadAllText(tempFile).Trim();
            Assert.True(int.TryParse(content, out int port) && port > 0);
            Assert.Equal(_server.BaseUri.Port, port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_PORT_FILE", original);
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void PortFilePath_UsesEnvOverride()
    {
        string? original = Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_PORT_FILE");
        try
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_PORT_FILE", "/tmp/custom-port.txt");
            Assert.Equal("/tmp/custom-port.txt", RealtimeMenuBarServer.PortFilePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_PORT_FILE", original);
        }
    }

    private sealed class StubMenuBarAssistantService : IMenuBarAssistantService
    {
        public string CurrentVoice => "alloy";
        public string CurrentThinkingLevel => "auto";
        public bool WakeWordEnabled => false;
        public string CurrentWakeWord => "Hey Jarvis";
        public IReadOnlyList<string> GetAvailableVoices() => [];
        public IReadOnlyList<string> GetAvailableThinkingLevels() => [];
        public Task<string> SetVoiceAsync(string voiceId, CancellationToken ct = default) => Task.FromResult(voiceId);
        public Task<string> SetThinkingLevelAsync(string thinkingLevel, CancellationToken ct = default) => Task.FromResult(thinkingLevel);
        public Task<(bool Enabled, string WakeWord)> SetWakeWordAsync(bool enabled, string wakeWord, CancellationToken ct = default) => Task.FromResult((enabled, wakeWord));
        public Task<RealtimeAssistantTurnResult> SendTextAsync(string text, CancellationToken ct = default) => Task.FromResult(new RealtimeAssistantTurnResult(text, null));
        public async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamTextAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
        public Task<RealtimeAssistantTurnResult> SendWaveAudioAsync(byte[] waveBytes, CancellationToken ct = default) => Task.FromResult(new RealtimeAssistantTurnResult(string.Empty, null));
        public async IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamWaveAudioAsync(byte[] waveBytes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
        public Task<string> StartLiveAudioInputAsync(CancellationToken ct = default) => Task.FromResult(Guid.NewGuid().ToString("N"));
        public Task AppendLiveAudioChunkAsync(string sessionId, byte[] pcmBytes, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<RealtimeAssistantStreamEvent> CommitLiveAudioInputAsync(string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
        public Task<bool> CancelLiveAudioInputAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CancelActiveTurnAsync(CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
