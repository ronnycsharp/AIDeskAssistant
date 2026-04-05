using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class RealtimeVoicePreferenceStoreTests
{
    [Fact]
    public void TryLoadVoice_WithMissingFile_ReturnsNull()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", tempFile);

            string? voice = RealtimeVoicePreferenceStore.TryLoadVoice();

            Assert.Null(voice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void SaveVoice_ThenTryLoadVoice_RoundTripsVoice()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", tempFile);

            RealtimeVoicePreferenceStore.SaveVoice("marin");

            string? voice = RealtimeVoicePreferenceStore.TryLoadVoice();

            Assert.Equal("marin", voice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void SaveThinkingLevel_ThenTryLoadThinkingLevel_RoundTripsLevel()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", tempFile);

            RealtimeVoicePreferenceStore.SaveThinkingLevel("high");

            string? thinkingLevel = RealtimeVoicePreferenceStore.TryLoadThinkingLevel();

            Assert.Equal("high", thinkingLevel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void SaveVoice_PreservesExistingThinkingLevel()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", tempFile);

            RealtimeVoicePreferenceStore.SaveThinkingLevel("medium");
            RealtimeVoicePreferenceStore.SaveVoice("marin");

            Assert.Equal("marin", RealtimeVoicePreferenceStore.TryLoadVoice());
            Assert.Equal("medium", RealtimeVoicePreferenceStore.TryLoadThinkingLevel());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void TryLoadVoice_WithInvalidJson_ReturnsNull()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", tempFile);
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            File.WriteAllText(tempFile, "not-json");

            string? voice = RealtimeVoicePreferenceStore.TryLoadVoice();

            Assert.Null(voice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    private static string CreateTempSettingsPath()
        => Path.Combine(Path.GetTempPath(), "AIDeskAssistant.Tests", $"voice-settings-{Guid.NewGuid():N}.json");

    private static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
}