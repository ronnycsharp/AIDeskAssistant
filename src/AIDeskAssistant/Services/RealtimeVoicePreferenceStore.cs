using System.Text.Json;

namespace AIDeskAssistant.Services;

internal static class RealtimeVoicePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsFilePath => Environment.GetEnvironmentVariable("AIDESK_REALTIME_SETTINGS_FILE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIDeskAssistant",
            "realtime-settings.json");

    public static string? TryLoadVoice()
    {
        SettingsFile? settings = TryReadSettings();
        string? voice = settings?.Voice?.Trim();
        return string.IsNullOrWhiteSpace(voice) ? null : voice;
    }

    public static string? TryLoadThinkingLevel()
    {
        SettingsFile? settings = TryReadSettings();
        return settings is null ? null : ThinkingLevelPreference.Normalize(settings.ThinkingLevel);
    }

    public static void SaveVoice(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required.", nameof(voice));

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);

        SettingsFile settings = TryReadSettings() ?? new SettingsFile();
        settings.Voice = voice.Trim();
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static void SaveThinkingLevel(string thinkingLevel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);

        SettingsFile settings = TryReadSettings() ?? new SettingsFile();
        settings.ThinkingLevel = ThinkingLevelPreference.Normalize(thinkingLevel);
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static SettingsFile? TryReadSettings()
    {
        if (!File.Exists(SettingsFilePath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(SettingsFilePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class SettingsFile
    {
        public string Voice { get; set; } = string.Empty;
        public string ThinkingLevel { get; set; } = ThinkingLevelPreference.Default;
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}