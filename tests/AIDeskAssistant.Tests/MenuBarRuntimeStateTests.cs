using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class MenuBarRuntimeStateTests
{
    [Fact]
    public void GetStatus_WithMissingFile_ReturnsNotRunning()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"aidesk-status-{Guid.NewGuid():N}.json");
        string? original = Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE", tempFile);

            MenuBarRuntimeStatus status = MenuBarRuntimeState.GetStatus();

            Assert.False(status.IsRunning);
            Assert.False(status.HasStateFile);
            Assert.Equal(tempFile, status.StatusFilePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE", original);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void RegisterCurrentProcess_ThenGetStatus_ReturnsRunning()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"aidesk-status-{Guid.NewGuid():N}.json");
        string? original = Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE", tempFile);
            MenuBarRuntimeState.RegisterCurrentProcess(new Uri("http://127.0.0.1:4242/"));

            MenuBarRuntimeStatus status = MenuBarRuntimeState.GetStatus();

            Assert.True(status.IsRunning);
            Assert.True(status.HasStateFile);
            Assert.Equal(Environment.ProcessId, status.ProcessId);
            Assert.Equal("http://127.0.0.1:4242/", status.ServerUri);
        }
        finally
        {
            MenuBarRuntimeState.ClearIfOwnedByCurrentProcess();
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE", original);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetStatus_WithStaleProcess_ReturnsNotRunningAndDeletesFile()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"aidesk-status-{Guid.NewGuid():N}.json");
        string? original = Environment.GetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE", tempFile);
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            File.WriteAllText(tempFile, """
            {
              "ProcessId": 999999,
              "ServerUri": "http://127.0.0.1:9999/",
              "StartedAtUtc": "2026-04-01T00:00:00+00:00"
            }
            """);

            MenuBarRuntimeStatus status = MenuBarRuntimeState.GetStatus();

            Assert.False(status.IsRunning);
            Assert.True(status.HasStateFile);
            Assert.Contains("stale", status.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_STATUS_FILE", original);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}