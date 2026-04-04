using OpenAI.Chat;
using System.Text.Json;

namespace AIDeskAssistant.Services;

internal sealed class AIDebugLogger
{
    private const int MaxToolActivityEntries = 40;
    private const string ToolActivityFileName = "menu-bar-tool-details.json";
    private const string PngMediaType = "image/png";
    private const string JpegMediaType = "image/jpeg";
    private static readonly JsonSerializerOptions ToolActivityJsonOptions = new() { WriteIndented = true };
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
    public string ToolActivityFilePath => Path.Combine(_sessionDirectoryPath, ToolActivityFileName);

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

    public void StartToolExecution(string toolCallId, string toolName, string argumentsJson)
    {
        lock (_syncRoot)
        {
            ToolActivityState state = ReadToolActivityState();
            ToolExecutionEntry entry = new()
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                ArgumentsJson = argumentsJson,
                Status = "running",
                StartedUtc = DateTimeOffset.UtcNow,
            };

            state.Entries.RemoveAll(existing => string.Equals(existing.ToolCallId, toolCallId, StringComparison.Ordinal));
            state.Entries.Add(entry);
            TrimToolActivityEntries(state);
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            WriteToolActivityState(state);
        }
    }

    public void CompleteToolExecution(string toolCallId, string result)
    {
        lock (_syncRoot)
        {
            ToolActivityState state = ReadToolActivityState();
            ToolExecutionEntry entry = GetOrCreateToolExecutionEntry(state, toolCallId);
            entry.Status = "completed";
            entry.Result = NormalizeToolResult(result);
            entry.CompletedUtc = DateTimeOffset.UtcNow;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            WriteToolActivityState(state);
        }
    }

    public void FailToolExecution(string toolCallId, string toolName, string argumentsJson, string error)
    {
        lock (_syncRoot)
        {
            ToolActivityState state = ReadToolActivityState();
            ToolExecutionEntry entry = GetOrCreateToolExecutionEntry(state, toolCallId);
            if (string.IsNullOrWhiteSpace(entry.ToolName))
                entry.ToolName = toolName;
            if (string.IsNullOrWhiteSpace(entry.ArgumentsJson))
                entry.ArgumentsJson = argumentsJson;

            entry.Status = "failed";
            entry.Result = error;
            entry.CompletedUtc = DateTimeOffset.UtcNow;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            WriteToolActivityState(state);
        }
    }

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
                JpegMediaType => ".jpg",
                PngMediaType => ".png",
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
                    JpegMediaType => ".jpg",
                    PngMediaType => ".png",
                    _ => ".bin",
                };

                string supplementalPath = Path.Combine(_sessionDirectoryPath, $"{fileStem}-{SanitizeFileName(supplementalImage.Label)}{supplementalExtension}");
                File.WriteAllBytes(supplementalPath, supplementalImage.Bytes);
            }

            File.WriteAllText(metaPath, $"Media type: {attachment.MediaType}{Environment.NewLine}History retention: {retentionStatus}{similaritySummary}{Environment.NewLine}{attachment.Summary}");
            AddScreenshotArtifact(toolCallId, new ScreenshotArtifact
            {
                Kind = "main",
                ImageFileName = relativeImagePath,
                MetaFileName = relativeMetaPath,
                Summary = attachment.Summary,
                MediaType = attachment.MediaType,
                RetentionStatus = retentionStatus,
                Similarity = similarityToPrevious?.ToString("P2"),
            });

            foreach (ScreenshotSupplementalImage supplementalImage in attachment.SupplementalImages)
            {
                string supplementalExtension = supplementalImage.MediaType switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    _ => ".bin",
                };

                string supplementalFileName = $"{fileStem}-{SanitizeFileName(supplementalImage.Label)}{supplementalExtension}";
                AddScreenshotArtifact(toolCallId, new ScreenshotArtifact
                {
                    Kind = supplementalImage.Label,
                    ImageFileName = supplementalFileName,
                    MetaFileName = null,
                    Summary = $"Supplemental image: {supplementalImage.Label}",
                    MediaType = supplementalImage.MediaType,
                    RetentionStatus = retentionStatus,
                    Similarity = null,
                });
            }

            LogHistoryEntry("screenshot", $"{retentionStatus} image={relativeImagePath} meta={relativeMetaPath} summary={attachment.Summary}{(similarityToPrevious.HasValue ? $" similarity={similarityToPrevious.Value:P2}" : string.Empty)}");
        }
    }

    public void LogScreenshotDifference(string toolCallId, byte[] diffBytes, string mediaType, string summary)
    {
        lock (_syncRoot)
        {
            string extension = mediaType switch
            {
                JpegMediaType => ".jpg",
                PngMediaType => ".png",
                _ => ".bin",
            };

            string fileStem = $"{_screenshotIndex:D2}-screenshot-diff-{SanitizeFileName(toolCallId)}";
            string imagePath = Path.Combine(_sessionDirectoryPath, fileStem + extension);
            string metaPath = Path.Combine(_sessionDirectoryPath, fileStem + ".txt");

            File.WriteAllBytes(imagePath, diffBytes);
            File.WriteAllText(metaPath, summary);
            AddScreenshotArtifact(toolCallId, new ScreenshotArtifact
            {
                Kind = "diff",
                ImageFileName = Path.GetFileName(imagePath),
                MetaFileName = Path.GetFileName(metaPath),
                Summary = summary,
                MediaType = mediaType,
                RetentionStatus = null,
                Similarity = null,
            });
            LogHistoryEntry("screenshot-diff", $"image={Path.GetFileName(imagePath)} meta={Path.GetFileName(metaPath)} summary={summary}");
        }
    }

    private void AddScreenshotArtifact(string toolCallId, ScreenshotArtifact artifact)
    {
        ToolActivityState state = ReadToolActivityState();
        ToolExecutionEntry entry = GetOrCreateToolExecutionEntry(state, toolCallId);
        entry.Screenshots.Add(artifact);
        state.LastUpdatedUtc = DateTimeOffset.UtcNow;
        WriteToolActivityState(state);
    }

    private void AppendLine(string fileName, string line)
    {
        lock (_syncRoot)
        {
            string filePath = Path.Combine(_sessionDirectoryPath, fileName);
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
    }

    private ToolActivityState ReadToolActivityState()
    {
        try
        {
            if (!File.Exists(ToolActivityFilePath))
                return new ToolActivityState();

            return JsonSerializer.Deserialize<ToolActivityState>(File.ReadAllText(ToolActivityFilePath), ToolActivityJsonOptions)
                ?? new ToolActivityState();
        }
        catch
        {
            return new ToolActivityState();
        }
    }

    private void WriteToolActivityState(ToolActivityState state)
    {
        Directory.CreateDirectory(_sessionDirectoryPath);
        File.WriteAllText(ToolActivityFilePath, JsonSerializer.Serialize(state, ToolActivityJsonOptions));
    }

    private static ToolExecutionEntry GetOrCreateToolExecutionEntry(ToolActivityState state, string toolCallId)
    {
        ToolExecutionEntry? entry = state.Entries.LastOrDefault(existing => string.Equals(existing.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (entry is not null)
            return entry;

        entry = new ToolExecutionEntry
        {
            ToolCallId = toolCallId,
            StartedUtc = DateTimeOffset.UtcNow,
            Status = "running",
        };
        state.Entries.Add(entry);
        TrimToolActivityEntries(state);
        return entry;
    }

    private static void TrimToolActivityEntries(ToolActivityState state)
    {
        if (state.Entries.Count > MaxToolActivityEntries)
            state.Entries.RemoveRange(0, state.Entries.Count - MaxToolActivityEntries);
    }

    private static string NormalizeToolResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return string.Empty;

        if (AIService.TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment) && attachment is not null)
            return attachment.Summary;

        return result.Length <= 24_000
            ? result
            : result[..24_000] + "\n... [tool result truncated in log]";
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

    private sealed class ToolActivityState
    {
        public DateTimeOffset LastUpdatedUtc { get; set; }
        public List<ToolExecutionEntry> Entries { get; set; } = [];
    }

    private sealed class ToolExecutionEntry
    {
        public string ToolCallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = string.Empty;
        public string Status { get; set; } = "running";
        public string Result { get; set; } = string.Empty;
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public List<ScreenshotArtifact> Screenshots { get; set; } = [];
    }

    private sealed class ScreenshotArtifact
    {
        public string Kind { get; set; } = string.Empty;
        public string ImageFileName { get; set; } = string.Empty;
        public string? MetaFileName { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string? MediaType { get; set; }
        public string? RetentionStatus { get; set; }
        public string? Similarity { get; set; }
    }
}