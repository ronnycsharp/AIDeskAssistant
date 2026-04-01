using OpenAI.Chat;

namespace AIDeskAssistant.Services;

internal sealed class AIDebugLogger
{
    private readonly object _syncRoot = new();
    private readonly string _sessionDirectoryPath;
    private int _userMessageIndex;
    private int _screenshotIndex;

    private AIDebugLogger(string sessionDirectoryPath)
    {
        _sessionDirectoryPath = sessionDirectoryPath;
        Directory.CreateDirectory(_sessionDirectoryPath);
    }

    public string SessionDirectoryPath => _sessionDirectoryPath;

    public static AIDebugLogger? CreateFromArgsAndEnvironment(IReadOnlyList<string> args)
    {
        bool enabled = args.Contains("--debug-model-io", StringComparer.OrdinalIgnoreCase)
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

    public void LogToolCall(string message)
        => AppendLine("tool-trace.log", $"CALL {message}");

    public void LogToolResult(string message)
        => AppendLine("tool-trace.log", $"RESULT {message}");

    public void LogAssistantResponse(string response)
        => AppendLine("assistant-response.log", response);

    public void LogScreenshotAttachment(string toolCallId, ScreenshotModelAttachment attachment)
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

            File.WriteAllBytes(imagePath, attachment.Bytes);
            File.WriteAllText(metaPath, $"Media type: {attachment.MediaType}{Environment.NewLine}{attachment.Summary}");
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
}