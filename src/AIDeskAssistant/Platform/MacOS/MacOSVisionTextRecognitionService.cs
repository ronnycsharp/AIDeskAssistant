using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSVisionTextRecognitionService : ITextRecognitionService
{
    private const string VisionScriptFileName = "AIDeskAssistantVisionOCR.swift";

    public TextRecognitionResult RecognizeText(byte[] imageBytes)
    {
        if (!TryResolveVisionScriptPath(out string scriptPath))
            throw new FileNotFoundException("The macOS Vision OCR helper script was not found in the application output.");

        string inputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant_ocr_{Guid.NewGuid():N}.png");

        try
        {
            File.WriteAllBytes(inputPath, imageBytes);

            ProcessStartInfo startInfo = new("xcrun")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("swift");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("--input");
            startInfo.ArgumentList.Add(inputPath);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch the Vision OCR helper.");

            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw new InvalidOperationException("Timed out while running the Vision OCR helper.");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(standardError) ? "Vision OCR helper failed." : standardError.Trim());

            VisionOcrPayload payload = JsonSerializer.Deserialize<VisionOcrPayload>(standardOutput)
                ?? throw new InvalidOperationException("Could not parse Vision OCR helper output.");

            IReadOnlyList<TextRecognitionLine> lines = payload.Lines
                .Select(static line => new TextRecognitionLine(
                    line.Text ?? string.Empty,
                    line.Confidence,
                    new WindowBounds(line.X, line.Y, line.Width, line.Height)))
                .ToList();

            return new TextRecognitionResult(payload.FullText?.Trim() ?? string.Empty, lines);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    private static bool TryResolveVisionScriptPath(out string scriptPath)
    {
        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, VisionScriptFileName),
            Path.Combine(AppContext.BaseDirectory, "Platform", "MacOS", VisionScriptFileName),
        ];

        string? existingPath = candidatePaths.FirstOrDefault(File.Exists);
        if (existingPath is not null)
        {
            scriptPath = existingPath;
            return true;
        }

        scriptPath = string.Empty;
        return false;
    }

    private sealed class VisionOcrPayload
    {
        public string? FullText { get; set; }
        public List<VisionOcrLine> Lines { get; set; } = [];
    }

    private sealed class VisionOcrLine
    {
        public string? Text { get; set; }
        public double Confidence { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}