using System.Text.Json;

namespace AIDeskAssistant.Services;

/// <summary>Stores and retrieves the preferred interaction language (de / en).</summary>
internal static class LanguagePreferenceStore
{
    public const string German = "de";
    public const string English = "en";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string _current = LoadInitial();

    public static string SettingsFilePath => Environment.GetEnvironmentVariable("AIDESK_SETTINGS_FILE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIDeskAssistant",
            "settings.json");

    /// <summary>Currently active language code ("de" or "en").</summary>
    public static string Current => _current;

    /// <summary>Display name of the current language for use inside system prompts ("German" / "English").</summary>
    public static string CurrentDisplayName => _current == English ? "English" : "German";

    /// <summary>Sets the language, persists it to disk, and returns the normalised code.</summary>
    public static string Set(string language)
    {
        string normalized = Normalize(language);
        _current = normalized;
        SaveToFile(normalized);
        Environment.SetEnvironmentVariable("AIDESK_LANGUAGE", normalized);
        return normalized;
    }

    /// <summary>Normalises a language string to "de" or "en" (defaults to "de").</summary>
    public static string Normalize(string? lang)
        => lang?.Trim().ToLowerInvariant() switch
        {
            "en" or "english" or "englisch" => English,
            _ => German,
        };

    // ── Internal ─────────────────────────────────────────────────────────────

    private static string LoadInitial()
        => Normalize(
            Environment.GetEnvironmentVariable("AIDESK_LANGUAGE")
            ?? TryReadFromFile());

    private static string? TryReadFromFile()
    {
        if (!File.Exists(SettingsFilePath))
            return null;
        try
        {
            var settings = JsonSerializer.Deserialize<SettingsFile>(
                File.ReadAllText(SettingsFilePath), JsonOptions);
            return settings?.Language?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void SaveToFile(string language)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsFilePath);
            if (dir is null) return;
            Directory.CreateDirectory(dir);
            SettingsFile settings = TryReadSettingsFile() ?? new SettingsFile();
            settings.Language = language;
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Non-fatal – preference will be in-memory only.
        }
    }

    private static SettingsFile? TryReadSettingsFile()
    {
        if (!File.Exists(SettingsFilePath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SettingsFile>(
                File.ReadAllText(SettingsFilePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class SettingsFile
    {
        public string Language { get; set; } = German;
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
