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
            FinalText: "Bereit.");

        object payload = RealtimeMenuBarServer.CreateStreamResponseEvent(streamEvent);
        JsonElement json = JsonSerializer.SerializeToElement(payload);

        Assert.Equal("completed", json.GetProperty("type").GetString());
        Assert.Equal("Bereit.", json.GetProperty("text").GetString());
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
}