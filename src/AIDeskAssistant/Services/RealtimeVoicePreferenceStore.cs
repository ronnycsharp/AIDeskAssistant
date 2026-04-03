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
        if (!File.Exists(SettingsFilePath))
            return null;

        try
        {
            VoicePreferenceFile? settings = JsonSerializer.Deserialize<VoicePreferenceFile>(File.ReadAllText(SettingsFilePath), JsonOptions);
            string? voice = settings?.Voice?.Trim();
            return string.IsNullOrWhiteSpace(voice) ? null : voice;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveVoice(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required.", nameof(voice));

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);

        VoicePreferenceFile settings = new()
        {
            Voice = voice.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private sealed class VoicePreferenceFile
    {
        public string Voice { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}