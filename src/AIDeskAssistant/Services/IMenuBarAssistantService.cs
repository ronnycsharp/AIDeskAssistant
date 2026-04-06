namespace AIDeskAssistant.Services;

internal interface IMenuBarAssistantService : IAsyncDisposable
{
    string CurrentVoice { get; }
    string CurrentThinkingLevel { get; }
    bool WakeWordEnabled { get; }
    string CurrentWakeWord { get; }
    IReadOnlyList<string> GetAvailableVoices();
    IReadOnlyList<string> GetAvailableThinkingLevels();
    Task<string> SetVoiceAsync(string voiceId, CancellationToken ct = default);
    Task<string> SetThinkingLevelAsync(string thinkingLevel, CancellationToken ct = default);
    Task<(bool Enabled, string WakeWord)> SetWakeWordAsync(bool enabled, string wakeWord, CancellationToken ct = default);
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