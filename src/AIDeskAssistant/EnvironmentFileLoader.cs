using System.Diagnostics.CodeAnalysis;

namespace AIDeskAssistant;

internal static class EnvironmentFileLoader
{
    private const string EnvFileName = ".env";

    public static string? LoadFromStandardLocations()
    {
        foreach (string directory in GetCandidateDirectories())
        {
            string path = Path.Combine(directory, EnvFileName);
            if (!File.Exists(path))
                continue;

            LoadFile(path);
            return path;
        }

        return null;
    }

    internal static void LoadFile(string filePath, bool overwriteExisting = false)
    {
        foreach (string line in File.ReadLines(filePath))
        {
            if (!TryParseAssignment(line, out string? key, out string? value))
                continue;

            if (!overwriteExisting && Environment.GetEnvironmentVariable(key) is not null)
                continue;

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    internal static bool TryParseAssignment(string line, [NotNullWhen(true)] out string? key, [NotNullWhen(true)] out string? value)
    {
        key = null;
        value = null;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        string trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
            return false;

        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..].TrimStart();

        int separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex <= 0)
            return false;

        key = trimmed[..separatorIndex].Trim();
        if (string.IsNullOrWhiteSpace(key))
            return false;

        value = trimmed[(separatorIndex + 1)..].Trim();

        if (value.Length >= 2)
        {
            bool isDoubleQuoted = value[0] == '"' && value[^1] == '"';
            bool isSingleQuoted = value[0] == '\'' && value[^1] == '\'';

            if (isDoubleQuoted || isSingleQuoted)
            {
                value = value[1..^1];
            }

            if (isDoubleQuoted)
            {
                value = value
                    .Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\r", "\r", StringComparison.Ordinal)
                    .Replace("\\t", "\t", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal);
            }
        }

        return true;
    }

    private static IEnumerable<string> GetCandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in EnumerateSelfAndParents(Directory.GetCurrentDirectory()))
        {
            if (seen.Add(directory))
                yield return directory;
        }

        foreach (string directory in EnumerateSelfAndParents(AppContext.BaseDirectory))
        {
            if (seen.Add(directory))
                yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateSelfAndParents(string startDirectory)
    {
        DirectoryInfo? current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }
}