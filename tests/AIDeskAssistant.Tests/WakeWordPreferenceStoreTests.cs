using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class WakeWordPreferenceStoreTests
{
    [Fact]
    public void TryLoadEnabled_WithMissingFile_ReturnsFalse()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);

            bool enabled = WakeWordPreferenceStore.TryLoadEnabled();

            Assert.False(enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void TryLoadWakeWord_WithMissingFile_ReturnsDefault()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);

            string wakeWord = WakeWordPreferenceStore.TryLoadWakeWord();

            Assert.Equal(WakeWordPreferenceStore.DefaultWakeWord, wakeWord);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void Save_ThenTryLoad_RoundTripsEnabledAndWakeWord()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);

            WakeWordPreferenceStore.Save(enabled: true, wakeWord: "Hey Computer");

            Assert.True(WakeWordPreferenceStore.TryLoadEnabled());
            Assert.Equal("Hey Computer", WakeWordPreferenceStore.TryLoadWakeWord());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void Save_DisabledWithCustomWord_PreservesWordWhenDisabled()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);

            WakeWordPreferenceStore.Save(enabled: true, wakeWord: "Hey Jarvis");
            WakeWordPreferenceStore.Save(enabled: false, wakeWord: "Hey Jarvis");

            Assert.False(WakeWordPreferenceStore.TryLoadEnabled());
            Assert.Equal("Hey Jarvis", WakeWordPreferenceStore.TryLoadWakeWord());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void TryLoadWakeWord_WithInvalidJson_ReturnsDefault()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            File.WriteAllText(tempFile, "not-json");

            string wakeWord = WakeWordPreferenceStore.TryLoadWakeWord();

            Assert.Equal(WakeWordPreferenceStore.DefaultWakeWord, wakeWord);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void Save_TrimsWhitespaceFromWakeWord()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);

            WakeWordPreferenceStore.Save(enabled: true, wakeWord: "  Hey Jarvis  ");

            Assert.Equal("Hey Jarvis", WakeWordPreferenceStore.TryLoadWakeWord());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    [Fact]
    public void Save_WithEmptyWakeWord_ThrowsArgumentException()
    {
        string tempFile = CreateTempSettingsPath();
        string? original = Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", tempFile);

            Assert.Throws<ArgumentException>(() => WakeWordPreferenceStore.Save(enabled: true, wakeWord: "   "));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE", original);
            DeleteIfExists(tempFile);
        }
    }

    private static string CreateTempSettingsPath()
        => Path.Combine(Path.GetTempPath(), "AIDeskAssistant.Tests", $"wakeword-settings-{Guid.NewGuid():N}.json");

    private static void DeleteIfExists(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
}
