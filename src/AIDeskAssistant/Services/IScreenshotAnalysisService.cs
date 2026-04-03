namespace AIDeskAssistant.Services;

internal interface IScreenshotAnalysisService
{
    Task<string?> AnalyzeAsync(ScreenshotModelAttachment attachment, CancellationToken ct = default);
}