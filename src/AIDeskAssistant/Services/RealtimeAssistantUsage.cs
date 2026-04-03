namespace AIDeskAssistant.Services;

internal sealed record RealtimeAssistantUsage(
    int? InputTokens = null,
    int? InputTextTokens = null,
    int? InputAudioTokens = null,
    int? InputImageTokens = null,
    int? CachedInputTokens = null,
    int? OutputTokens = null,
    int? OutputTextTokens = null,
    int? OutputAudioTokens = null,
    int? TotalTokens = null)
{
    public RealtimeAssistantUsage Add(RealtimeAssistantUsage? other)
    {
        if (other is null)
            return this;

        return new RealtimeAssistantUsage(
            Sum(InputTokens, other.InputTokens),
            Sum(InputTextTokens, other.InputTextTokens),
            Sum(InputAudioTokens, other.InputAudioTokens),
            Sum(InputImageTokens, other.InputImageTokens),
            Sum(CachedInputTokens, other.CachedInputTokens),
            Sum(OutputTokens, other.OutputTokens),
            Sum(OutputTextTokens, other.OutputTextTokens),
            Sum(OutputAudioTokens, other.OutputAudioTokens),
            Sum(TotalTokens, other.TotalTokens));
    }

    private static int? Sum(int? left, int? right)
    {
        if (!left.HasValue && !right.HasValue)
            return null;

        return (left ?? 0) + (right ?? 0);
    }
}