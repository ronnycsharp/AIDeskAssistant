namespace AIDeskAssistant.Services;

internal interface IMenuBarAssistantService : IAsyncDisposable
{
    string CurrentVoice { get; }
    string CurrentThinkingLevel { get; }
    string CurrentLanguage { get; }
    bool IsMuted { get; }
    IReadOnlyList<string> GetAvailableVoices();
    IReadOnlyList<string> GetAvailableThinkingLevels();
    IReadOnlyList<string> GetAvailableLanguages();
    Task<string> SetVoiceAsync(string voiceId, CancellationToken ct = default);
    Task<string> SetThinkingLevelAsync(string thinkingLevel, CancellationToken ct = default);
    Task SetLanguageAsync(string language, CancellationToken ct = default);
    Task SetApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task<bool> SetMutedAsync(bool muted, CancellationToken ct = default);
    Task<RealtimeAssistantTurnResult> SendTextAsync(string text, CancellationToken ct = default);
    IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamTextAsync(string text, CancellationToken ct = default);
    Task<RealtimeAssistantTurnResult> SendWaveAudioAsync(byte[] waveBytes, CancellationToken ct = default);
    IAsyncEnumerable<RealtimeAssistantStreamEvent> StreamWaveAudioAsync(byte[] waveBytes, CancellationToken ct = default);
    Task<string> StartLiveAudioInputAsync(CancellationToken ct = default);
    Task AppendLiveAudioChunkAsync(string sessionId, byte[] pcmBytes, CancellationToken ct = default);
    IAsyncEnumerable<RealtimeAssistantStreamEvent> CommitLiveAudioInputAsync(string sessionId, CancellationToken ct = default);
    Task<bool> CancelLiveAudioInputAsync(string sessionId, CancellationToken ct = default);
    Task<bool> CancelActiveTurnAsync(CancellationToken ct = default);
}