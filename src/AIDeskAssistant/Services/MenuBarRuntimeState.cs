using System.Diagnostics;
using System.Text.Json;

namespace AIDeskAssistant.Services;

internal static class MenuBarRuntimeState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string StatusFilePath => Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE")
        ?? Path.Combine(Path.GetTempPath(), "AIDeskAssistant", "menu-bar-status.json");

    public static void RegisterCurrentProcess(Uri serverUri)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusFilePath)!);

        RuntimeStateFile state = new()
        {
            ProcessId = Environment.ProcessId,
            ServerUri = serverUri.AbsoluteUri,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        File.WriteAllText(StatusFilePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    public static void ClearIfOwnedByCurrentProcess()
    {
        if (!File.Exists(StatusFilePath))
            return;

        RuntimeStateFile? state = TryReadStateFile();
        if (state?.ProcessId == Environment.ProcessId)
            File.Delete(StatusFilePath);
    }

    public static MenuBarRuntimeStatus GetStatus()
    {
        if (!File.Exists(StatusFilePath))
            return new(false, false, null, null, null, StatusFilePath, "No menu bar status file found.");

        RuntimeStateFile? state = TryReadStateFile();
        if (state is null)
            return new(true, false, null, null, null, StatusFilePath, "Status file exists but could not be parsed.");

        bool isRunning = IsProcessRunning(state.ProcessId);
        if (!isRunning)
        {
            TryDeleteStaleStateFile(state.ProcessId);
            return new(true, false, state.ProcessId, state.ServerUri, state.StartedAtUtc, StatusFilePath, "Status file was stale; the recorded process is no longer running.");
        }

        return new(true, true, state.ProcessId, state.ServerUri, state.StartedAtUtc, StatusFilePath, "Menu bar host is running.");
    }

    public static bool TryStopRunningHost(out string message)
    {
        MenuBarRuntimeStatus status = GetStatus();
        if (!status.IsRunning || status.ProcessId is null)
        {
            message = status.Detail ?? "Menu bar host is not running.";
            return false;
        }

        try
        {
            Process process = Process.GetProcessById(status.ProcessId.Value);
            process.Kill(entireProcessTree: true);
            message = $"Stopped menu bar host process {status.ProcessId.Value}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to stop menu bar host process {status.ProcessId.Value}: {ex.Message}";
            return false;
        }
    }

    private static RuntimeStateFile? TryReadStateFile()
    {
        try
        {
            return JsonSerializer.Deserialize<RuntimeStateFile>(File.ReadAllText(StatusFilePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteStaleStateFile(int processId)
    {
        RuntimeStateFile? current = TryReadStateFile();
        if (current?.ProcessId == processId && File.Exists(StatusFilePath))
            File.Delete(StatusFilePath);
    }

    private sealed class RuntimeStateFile
    {
        public int ProcessId { get; set; }
        public string ServerUri { get; set; } = string.Empty;
        public DateTimeOffset StartedAtUtc { get; set; }
    }
}

internal sealed record MenuBarRuntimeStatus(
    bool HasStateFile,
    bool IsRunning,
    int? ProcessId,
    string? ServerUri,
    DateTimeOffset? StartedAtUtc,
    string StatusFilePath,
    string? Detail);