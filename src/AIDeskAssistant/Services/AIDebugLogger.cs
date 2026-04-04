using OpenAI.Chat;

namespace AIDeskAssistant.Services;

internal sealed class AIDebugLogger
{
    private readonly object _syncRoot = new();
    private readonly string _sessionDirectoryPath;
    private int _historyIndex;
    private int _userMessageIndex;
    private int _uiContextIndex;
    private int _screenshotIndex;

    private AIDebugLogger(string sessionDirectoryPath)
    {
        _sessionDirectoryPath = sessionDirectoryPath;
        Directory.CreateDirectory(_sessionDirectoryPath);
    }

    public string SessionDirectoryPath => _sessionDirectoryPath;

    public static AIDebugLogger? CreateFromArgsAndEnvironment(IReadOnlyList<string> args)
        => CreateFromArgsAndEnvironment(args, forceEnabled: false);

    public static AIDebugLogger? CreateFromArgsAndEnvironment(IReadOnlyList<string> args, bool forceEnabled)
    {
        bool enabled = forceEnabled
            || args.Contains("--debug-model-io", StringComparer.OrdinalIgnoreCase)
            || IsTruthy(Environment.GetEnvironmentVariable("AIDESK_DEBUG_MODEL_IO"));

        if (!enabled)
            return null;

        string baseDirectory = Environment.GetEnvironmentVariable("AIDESK_DEBUG_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".aidesk-debug");
        string sessionDirectory = Path.Combine(baseDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        return new AIDebugLogger(sessionDirectory);
    }

    public void LogPreparedUserMessage(string message)
    {
        lock (_syncRoot)
        {
            _userMessageIndex++;
            string filePath = Path.Combine(_sessionDirectoryPath, $"{_userMessageIndex:D2}-user-message.txt");
            File.WriteAllText(filePath, message);
        }
    }

    public void LogUiContext(string context)
    {
        lock (_syncRoot)
        {
            _uiContextIndex++;
            string filePath = Path.Combine(_sessionDirectoryPath, $"{_uiContextIndex:D2}-ui-context.txt");
            File.WriteAllText(filePath, context);
        }

        LogHistoryEntry("ui", context);
    }

    public void LogToolCall(string message)
        => AppendLine("tool-trace.log", $"CALL {message}");

    public void LogToolResult(string message)
        => AppendLine("tool-trace.log", $"RESULT {message}");

    public void LogAssistantResponse(string response)
        => AppendLine("assistant-response.log", response);

    public void LogHistoryEntry(string role, string content)
    {
        lock (_syncRoot)
        {
            _historyIndex++;
            string normalizedContent = NormalizeSingleLine(content, 4_000);
            string filePath = Path.Combine(_sessionDirectoryPath, "history.log");
            File.AppendAllText(filePath, $"[{_historyIndex:D3}] {role.ToUpperInvariant()}: {normalizedContent}{Environment.NewLine}");
        }
    }

    public void LogScreenshotAttachment(string toolCallId, ScreenshotModelAttachment attachment, bool retainedInHistory, double? similarityToPrevious)
    {
        lock (_syncRoot)
        {
            _screenshotIndex++;
            string extension = attachment.MediaType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                _ => ".bin",
            };

            string fileStem = $"{_screenshotIndex:D2}-screenshot-{SanitizeFileName(toolCallId)}";
            string imagePath = Path.Combine(_sessionDirectoryPath, fileStem + extension);
            string metaPath = Path.Combine(_sessionDirectoryPath, fileStem + ".txt");
            string relativeImagePath = Path.GetFileName(imagePath);
            string relativeMetaPath = Path.GetFileName(metaPath);
            string retentionStatus = retainedInHistory ? "retained" : "omitted-from-history";
            string similaritySummary = similarityToPrevious.HasValue
                ? $"{Environment.NewLine}Similarity to previous retained screenshot: {similarityToPrevious.Value:P2}"
                : string.Empty;

            File.WriteAllBytes(imagePath, attachment.Bytes);
            foreach (ScreenshotSupplementalImage supplementalImage in attachment.SupplementalImages)
            {
                string supplementalExtension = supplementalImage.MediaType switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    _ => ".bin",
                };

                string supplementalPath = Path.Combine(_sessionDirectoryPath, $"{fileStem}-{SanitizeFileName(supplementalImage.Label)}{supplementalExtension}");
                File.WriteAllBytes(supplementalPath, supplementalImage.Bytes);
            }

            File.WriteAllText(metaPath, $"Media type: {attachment.MediaType}{Environment.NewLine}History retention: {retentionStatus}{similaritySummary}{Environment.NewLine}{attachment.Summary}");
            LogHistoryEntry("screenshot", $"{retentionStatus} image={relativeImagePath} meta={relativeMetaPath} summary={attachment.Summary}{(similarityToPrevious.HasValue ? $" similarity={similarityToPrevious.Value:P2}" : string.Empty)}");
        }
    }

    public void LogScreenshotDifference(string toolCallId, byte[] diffBytes, string mediaType, string summary)
    {
        lock (_syncRoot)
        {
            string extension = mediaType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                _ => ".bin",
            };

            string fileStem = $"{_screenshotIndex:D2}-screenshot-diff-{SanitizeFileName(toolCallId)}";
            string imagePath = Path.Combine(_sessionDirectoryPath, fileStem + extension);
            string metaPath = Path.Combine(_sessionDirectoryPath, fileStem + ".txt");

            File.WriteAllBytes(imagePath, diffBytes);
            File.WriteAllText(metaPath, summary);
            LogHistoryEntry("screenshot-diff", $"image={Path.GetFileName(imagePath)} meta={Path.GetFileName(metaPath)} summary={summary}");
        }
    }

    private void AppendLine(string fileName, string line)
    {
        lock (_syncRoot)
        {
            string filePath = Path.Combine(_sessionDirectoryPath, fileName);
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
    }

    private static bool IsTruthy(string? value)
        => value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(static ch => ch).Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string NormalizeSingleLine(string content, int maxLength)
    {
        string normalized = content.ReplaceLineEndings(" ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;

        return normalized[..maxLength] + "…";
    }
}