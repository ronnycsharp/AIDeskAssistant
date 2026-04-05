using OpenAI.Chat;

namespace AIDeskAssistant.Services;

internal static class ThinkingLevelPreference
{
    public const string Default = "default";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    private static readonly string[] AvailableLevels = [Default, Low, Medium, High];

    public static IReadOnlyList<string> GetAvailableLevels() => AvailableLevels;

    public static string Normalize(string? level)
    {
        string normalized = level?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            Low => Low,
            Medium => Medium,
            High => High,
            _ => Default,
        };
    }

    public static bool SupportsReasoningEffort(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        string normalized = model.Trim().ToLowerInvariant();
        return normalized.Contains("gpt-5", StringComparison.Ordinal)
            || normalized.Contains("o1", StringComparison.Ordinal)
            || normalized.Contains("o3", StringComparison.Ordinal)
            || normalized.Contains("o4", StringComparison.Ordinal);
    }

    public static void ApplyTo(ChatCompletionOptions options, string model, string? level)
    {
        if (!SupportsReasoningEffort(model))
            return;

        string normalized = Normalize(level);
        if (normalized == Default)
            return;

        options.ReasoningEffortLevel = normalized switch
        {
            Low => ChatReasoningEffortLevel.Low,
            Medium => ChatReasoningEffortLevel.Medium,
            High => ChatReasoningEffortLevel.High,
            _ => options.ReasoningEffortLevel,
        };
    }
}