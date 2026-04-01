namespace AIDeskAssistant.Services;

internal sealed record RealtimeAssistantTurnResult(string Text, byte[]? AudioWavBytes);