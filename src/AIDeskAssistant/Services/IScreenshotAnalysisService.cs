namespace AIDeskAssistant.Services;

internal interface IScreenshotAnalysisService
{
    string CurrentThinkingLevel { get; }
    IReadOnlyList<string> GetAvailableThinkingLevels();
    string SetThinkingLevel(string thinkingLevel);
    Task<string?> AnalyzeAsync(ScreenshotModelAttachment attachment, CancellationToken ct = default);
}