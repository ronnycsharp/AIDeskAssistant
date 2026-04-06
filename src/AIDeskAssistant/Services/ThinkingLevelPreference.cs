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
        => SupportsReasoningEffort(model, usesFunctionTools: false);

    public static bool SupportsReasoningEffort(string model, bool usesFunctionTools)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        string normalized = model.Trim().ToLowerInvariant();
        if (usesFunctionTools && normalized.StartsWith("gpt-5.4", StringComparison.Ordinal))
            return false;

        return normalized.Contains("gpt-5", StringComparison.Ordinal)
            || normalized.Contains("o1", StringComparison.Ordinal)
            || normalized.Contains("o3", StringComparison.Ordinal)
            || normalized.Contains("o4", StringComparison.Ordinal);
    }

    public static void ApplyTo(ChatCompletionOptions options, string model, string? level)
    {
        if (!SupportsReasoningEffort(model, options.Tools.Count > 0))
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