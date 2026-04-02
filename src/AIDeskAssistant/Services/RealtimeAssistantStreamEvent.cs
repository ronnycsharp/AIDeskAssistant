namespace AIDeskAssistant.Services;

internal enum RealtimeAssistantStreamEventType
{
    TextDelta,
    AudioDelta,
    Completed,
    Error,
}

internal sealed record RealtimeAssistantStreamEvent(
    RealtimeAssistantStreamEventType Type,
    string? TextDelta = null,
    byte[]? AudioPcmBytes = null,
    string? FinalText = null,
    string? ErrorMessage = null);