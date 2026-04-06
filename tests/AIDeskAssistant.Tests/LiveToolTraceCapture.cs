using System.Text.RegularExpressions;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

internal sealed class LiveToolTraceCapture
{
    private readonly string _resultsDirectory;
    private int _toolCallSequence;
    private string _lastToolName = "tool";

    public LiveToolTraceCapture(string resultsDirectory)
    {
        _resultsDirectory = resultsDirectory;
    }

    public void HandleToolCall(string message)
    {
        _toolCallSequence++;
        _lastToolName = TryGetToolName(message) ?? "tool";
        Console.WriteLine($"[tool-call {_toolCallSequence:00}] {message}");
    }

    public void HandleToolResult(string message)
    {
        Console.WriteLine($"[tool-result {_toolCallSequence:00}] {message}");

        if (!AIService.TryParseScreenshotAttachment(message, out ScreenshotModelAttachment? attachment)
            || attachment is null)
        {
            return;
        }

        string prefix = $"{_toolCallSequence:00}_{SanitizeFileName(_lastToolName)}";
        SaveImage(prefix + "_primary", attachment.Bytes, attachment.MediaType);
        File.WriteAllText(Path.Combine(_resultsDirectory, prefix + "_summary.txt"), attachment.Summary);

        foreach (ScreenshotSupplementalImage supplementalImage in attachment.SupplementalImages)
            SaveImage(prefix + "_" + SanitizeFileName(supplementalImage.Label), supplementalImage.Bytes, supplementalImage.MediaType);
    }

    private void SaveImage(string baseName, byte[] bytes, string mediaType)
    {
        string extension = mediaType.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };

        File.WriteAllBytes(Path.Combine(_resultsDirectory, baseName + extension), bytes);
    }

    private static string? TryGetToolName(string message)
    {
        Match match = Regex.Match(message, @"Tool:\s*(?<name>[A-Za-z0-9_]+)\(", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "artifact";

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (invalidChars.Contains(character) || !char.IsLetterOrDigit(character) && character is not '_' and not '-')
                builder.Append('_');
            else
                builder.Append(character);
        }

        return builder.ToString().Trim('_');
    }
}