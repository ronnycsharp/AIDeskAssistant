using System.Text.Json;

namespace AIDeskAssistant.Services;

internal static class MenuBarActivityState
{
    private const int MaxEntries = 20;
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ActivityFilePath => Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_ACTIVITY_FILE")
        ?? Path.Combine(Path.GetTempPath(), "AIDeskAssistant", "menu-bar-activity.json");

    public static void Reset(string currentStep, string? initialEvent = null)
    {
        lock (SyncRoot)
        {
            ActivityStateFile state = new()
            {
                CurrentStep = currentStep,
                LastUpdatedUtc = DateTimeOffset.UtcNow,
            };

            if (!string.IsNullOrWhiteSpace(initialEvent))
                state.Entries.Add(CreateEntry(initialEvent, "info", null));

            WriteState(state);
        }
    }

    public static void UpdateStep(string currentStep, string? activeTool = null, string? eventMessage = null, string eventKind = "info")
    {
        lock (SyncRoot)
        {
            ActivityStateFile state = ReadState() ?? new ActivityStateFile();
            state.CurrentStep = currentStep;
            state.ActiveTool = activeTool;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;

            if (!string.IsNullOrWhiteSpace(eventMessage))
                AddEntry(state, CreateEntry(eventMessage, eventKind, activeTool));

            WriteState(state);
        }
    }

    public static void ToolStarted(string toolName, string? detail = null)
    {
        string message = string.IsNullOrWhiteSpace(detail)
            ? $"Tool gestartet: {toolName}"
            : $"Tool gestartet: {toolName} ({detail})";
        UpdateStep($"Tool läuft: {toolName}", toolName, message, "tool_started");
    }

    public static void ToolFinished(string toolName, string? resultSummary = null)
    {
        string message = string.IsNullOrWhiteSpace(resultSummary)
            ? $"Tool beendet: {toolName}"
            : $"Tool beendet: {toolName} ({resultSummary})";
        UpdateStep($"Tool beendet: {toolName}", null, message, "tool_finished");
    }

    public static void ToolFailed(string toolName, string error)
        => UpdateStep($"Toolfehler: {toolName}", null, $"Toolfehler: {toolName} ({error})", "error");

    public static void Clear()
    {
        lock (SyncRoot)
        {
            if (File.Exists(ActivityFilePath))
                File.Delete(ActivityFilePath);
        }
    }

    private static ActivityStateFile? ReadState()
    {
        try
        {
            if (!File.Exists(ActivityFilePath))
                return null;

            return JsonSerializer.Deserialize<ActivityStateFile>(File.ReadAllText(ActivityFilePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(ActivityStateFile state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ActivityFilePath)!);
        File.WriteAllText(ActivityFilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void AddEntry(ActivityStateFile state, ActivityEntry entry)
    {
        state.Entries.Add(entry);
        if (state.Entries.Count > MaxEntries)
            state.Entries.RemoveRange(0, state.Entries.Count - MaxEntries);
    }

    private static ActivityEntry CreateEntry(string message, string kind, string? toolName)
        => new()
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Message = message,
            Kind = kind,
            ToolName = toolName,
        };

    private sealed class ActivityStateFile
    {
        public string CurrentStep { get; set; } = "Leerlauf";
        public string? ActiveTool { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; }
        public List<ActivityEntry> Entries { get; } = [];
    }

    private sealed class ActivityEntry
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Kind { get; set; } = "info";
        public string? ToolName { get; set; }
    }
}