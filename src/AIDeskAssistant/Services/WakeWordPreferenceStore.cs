using System.Text.Json;

namespace AIDeskAssistant.Services;

internal static class WakeWordPreferenceStore
{
    internal const string DefaultWakeWord = "Hey Jarvis";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsFilePath => Environment.GetEnvironmentVariable("AIDESK_WAKEWORD_SETTINGS_FILE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIDeskAssistant",
            "wakeword-settings.json");

    public static bool TryLoadEnabled()
    {
        return TryReadSettings()?.Enabled ?? false;
    }

    public static string TryLoadWakeWord()
    {
        string? word = TryReadSettings()?.WakeWord?.Trim();
        return string.IsNullOrWhiteSpace(word) ? DefaultWakeWord : word;
    }

    public static void Save(bool enabled, string wakeWord)
    {
        if (string.IsNullOrWhiteSpace(wakeWord))
            throw new ArgumentException("Wake word is required.", nameof(wakeWord));

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);

        SettingsFile settings = TryReadSettings() ?? new SettingsFile();
        settings.Enabled = enabled;
        settings.WakeWord = wakeWord.Trim();
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
        public bool Enabled { get; set; } = false;
        public string WakeWord { get; set; } = DefaultWakeWord;
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
