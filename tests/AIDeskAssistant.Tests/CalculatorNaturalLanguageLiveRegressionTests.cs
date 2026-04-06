using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AIDeskAssistant.Models;
using AIDeskAssistant.Platform.MacOS;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Tests;

public sealed class CalculatorNaturalLanguageLiveRegressionTests
{
    private const string LiveLlmRegressionOptInVariable = "AIDESK_ENABLE_LIVE_CALCULATOR_LLM_REGRESSION_TESTS";

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task CalculatorRuntimeLlmRegression_ComputesSquareRootOfFortyNineFromNaturalLanguageInstruction()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (!string.Equals(Environment.GetEnvironmentVariable(LiveLlmRegressionOptInVariable), "1", StringComparison.Ordinal))
            return;

        _ = EnvironmentFileLoader.LoadFromStandardLocations();
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
        string resultsDirectory = CreateLiveResultsDirectory("calculator-llm-natural-language-sqrt-49");

        QuitCalculatorIfRunning();

        var screenshot = new MacOSScreenshotService();
        var mouse = new MacOSMouseService();
        var keyboard = new MacOSKeyboardService();
        var terminal = new ProcessTerminalService();
        var window = new MacOSWindowService();
        var uiAutomation = new MacOSUiAutomationService();
        var textRecognition = new MacOSVisionTextRecognitionService();
        var executor = new DesktopToolExecutor(screenshot, mouse, keyboard, terminal, window, uiAutomation, textRecognition);
        var assistant = new AIService(apiKey, executor, model);

        var toolTrace = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        string finalResponse = await assistant.SendMessageAsync(
            "Öffne den Rechner auf macOS und berechne die Quadratwurzel von 49. Entscheide selbst, welche Zwischenschritte und welcher Rechner-Modus nötig sind. Verwende dafür den Rechner selbst und nicht Terminal, Python, Shell-Kommandos oder eine externe Berechnung. Setze nicht einfach das Ergebnis 7 manuell in die Anzeige, sondern führe die eigentliche Rechenoperation im Rechner aus. Nutze vor und nach zustandsändernden Aktionen passende Prüfungen und bestätige den Erfolg erst, wenn im Rechner sichtbar 7 steht.",
            onToolCall: message => toolTrace.Add($"CALL {message}"),
            onToolResult: message => toolTrace.Add($"RESULT {message}"),
            maxToolRounds: 80,
            ct: cts.Token);

        await File.WriteAllLinesAsync(Path.Combine(resultsDirectory, "tool-trace.txt"), toolTrace, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "assistant-final-response.txt"), finalResponse, cts.Token);

        string toolTraceText = string.Join(Environment.NewLine, toolTrace);
        Assert.DoesNotContain("run_command(", toolTraceText, StringComparison.Ordinal);
        Assert.DoesNotContain("open_application({\"name\":\"Terminal\"})", toolTraceText, StringComparison.Ordinal);
        Assert.Contains(toolTrace, entry => entry.Contains("49", StringComparison.Ordinal));
        Assert.Contains(
            toolTrace,
            entry => entry.Contains("wurzel", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("sqrt", StringComparison.OrdinalIgnoreCase)
                || entry.Contains("√", StringComparison.Ordinal));

        _ = Retry(
            () =>
            {
                window.FocusWindow("Calculator", null);
                return window.GetFrontmostApplicationName();
            },
            appName => appName.Contains("Calculator", StringComparison.OrdinalIgnoreCase)
                || appName.Contains("Rechner", StringComparison.OrdinalIgnoreCase),
            "Calculator did not end up frontmost after the natural-language run.");

        WindowBounds activeWindowBounds = Retry(
            () => window.GetActiveWindowBounds(),
            bounds => bounds.Width > 0 && bounds.Height > 0,
            "Could not resolve Calculator active window bounds after the natural-language run.");
        string finalUiSummary = Retry(
            uiAutomation.SummarizeFrontmostUiElements,
            summary => summary.Contains("AXButton", StringComparison.Ordinal),
            "Could not resolve calculator AX summary after the natural-language run.");
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "final-ui-summary.txt"), finalUiSummary, cts.Token);

        WindowBounds displayBounds = TryGetCalculatorDisplayBoundsFromUiSummary(finalUiSummary)
            ?? activeWindowBounds;
        byte[] finalScreenshotBytes = CaptureScreenshotWithPython(displayBounds);
        await File.WriteAllBytesAsync(Path.Combine(resultsDirectory, "final-window.png"), finalScreenshotBytes, cts.Token);

        string axDisplayText = TryGetCalculatorDisplayTextFromUiSummary(finalUiSummary) ?? string.Empty;
        string ocrDisplayText = NormalizeCalculatorDisplayText(textRecognition.RecognizeText(finalScreenshotBytes).FullText);
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "final-ocr.txt"), ocrDisplayText, cts.Token);

        Assert.True(
            MatchesExpectedCalculatorResult(axDisplayText, "7")
                || MatchesExpectedCalculatorResult(ocrDisplayText, "7"),
            $"Expected calculator display to show 7 after natural-language run, but AX was '{axDisplayText}' and OCR was '{ocrDisplayText}'. Final response: {finalResponse}");
    }

    [Fact]
    public void TryGetCalculatorDisplayTextFromUiSummary_PrefersResolvedDisplayValue()
    {
        string uiSummary = """
            Frontmost app: Rechner
            Focused window: Rechner at x=628,y=352,w=674,h=408
            Visible UI elements:
            - AXStaticText | value=49 | x=1254,y=441,w=38,h=36
            - AXButton | calculator_key=7 | x=1100,y=539,w=60,h=48
            """;

        string? displayText = TryGetCalculatorDisplayTextFromUiSummary(uiSummary);

        Assert.Equal("49", displayText);
    }

    [Theory]
    [InlineData("7", "7", true)]
    [InlineData("7=", "7", true)]
    [InlineData("49", "7", false)]
    [InlineData("107", "7", false)]
    [InlineData("", "7", false)]
    public void MatchesExpectedCalculatorResult_RequiresExactResolvedDisplayValue(string actual, string expected, bool matches)
    {
        Assert.Equal(matches, MatchesExpectedCalculatorResult(actual, expected));
    }

    private static string CreateLiveResultsDirectory(string folderName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestResults", folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    [SupportedOSPlatform("macos")]
    private static void QuitCalculatorIfRunning()
    {
        ProcessStartInfo startInfo = new("/usr/bin/osascript")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("tell application \"Calculator\" to quit");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to quit Calculator.");
        process.WaitForExit(10_000);
        Thread.Sleep(500);
    }

    [SupportedOSPlatform("macos")]
    private static byte[] CaptureScreenshotWithPython(WindowBounds bounds)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant-calculator-natural-{Guid.NewGuid():N}.png");
        try
        {
            ProcessStartInfo startInfo = new("/usr/bin/python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import subprocess, sys; x, y, w, h, out = sys.argv[1:6]; subprocess.run(['/usr/sbin/screencapture', '-x', '-C', f'-R{x},{y},{w},{h}', out], check=True)");
            startInfo.ArgumentList.Add(bounds.X.ToString());
            startInfo.ArgumentList.Add(bounds.Y.ToString());
            startInfo.ArgumentList.Add(bounds.Width.ToString());
            startInfo.ArgumentList.Add(bounds.Height.ToString());
            startInfo.ArgumentList.Add(outputPath);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Python runtime for screenshot capture.");
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Python screenshot capture failed: {standardError.Trim()}");

            Assert.True(File.Exists(outputPath), $"Expected runtime screenshot at '{outputPath}'.");
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static string? TryGetCalculatorDisplayTextFromUiSummary(string uiSummary)
    {
        MatchCollection staticTextMatches = Regex.Matches(
            uiSummary,
            @"AXStaticText\s*\|\s*value=(?<value>.*?)\s*\|\s*x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        var candidates = staticTextMatches.Cast<Match>()
            .Where(static match => match.Success)
            .Select(static match => new
            {
                Text = NormalizeCalculatorDisplayText(match.Groups["value"].Value),
                Y = int.Parse(match.Groups["y"].Value),
                Height = int.Parse(match.Groups["h"].Value),
            })
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .ToList();

        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(static candidate => IsResolvedCalculatorDisplayText(candidate.Text))
            .ThenBy(static candidate => candidate.Text.Length)
            .ThenByDescending(static candidate => candidate.Y)
            .ThenByDescending(static candidate => candidate.Height)
            .Select(static candidate => candidate.Text)
            .FirstOrDefault();
    }

    private static WindowBounds? TryGetCalculatorDisplayBoundsFromUiSummary(string uiSummary)
    {
        MatchCollection staticTextMatches = Regex.Matches(
            uiSummary,
            @"AXStaticText\s*\|\s*value=(?<value>.*?)\s*\|\s*x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        var candidates = staticTextMatches.Cast<Match>()
            .Where(static match => match.Success)
            .Select(static match => new
            {
                Text = NormalizeCalculatorDisplayText(match.Groups["value"].Value),
                Bounds = new WindowBounds(
                    int.Parse(match.Groups["x"].Value),
                    int.Parse(match.Groups["y"].Value),
                    int.Parse(match.Groups["w"].Value),
                    int.Parse(match.Groups["h"].Value))
            })
            .Where(static candidate => IsResolvedCalculatorDisplayText(candidate.Text))
            .OrderBy(static candidate => candidate.Text.Length)
            .ThenByDescending(static candidate => candidate.Bounds.Y)
            .ToList();

        if (candidates.Count == 0)
            return null;

        WindowBounds displayBounds = candidates[0].Bounds;
        return new WindowBounds(
            Math.Max(0, displayBounds.X - 12),
            Math.Max(0, displayBounds.Y - 4),
            Math.Max(1, displayBounds.Width + 24),
            Math.Max(1, displayBounds.Height + 12));
    }

    private static string NormalizeCalculatorDisplayText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = text
            .Replace("−", "-", StringComparison.Ordinal)
            .Replace("×", "*", StringComparison.Ordinal)
            .Replace("÷", "/", StringComparison.Ordinal)
            .Replace("＝", "=", StringComparison.Ordinal)
            .ReplaceLineEndings(string.Empty);

        return Regex.Replace(normalized, @"[^0-9+*/=.-]", string.Empty, RegexOptions.CultureInvariant);
    }

    private static bool IsResolvedCalculatorDisplayText(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"^-?\d+(?:\.\d+)?=?$", RegexOptions.CultureInvariant);

    private static bool MatchesExpectedCalculatorResult(string? actual, string expected)
    {
        string normalizedActual = NormalizeResolvedCalculatorResult(actual);
        string normalizedExpected = NormalizeResolvedCalculatorResult(expected);
        return !string.IsNullOrWhiteSpace(normalizedActual)
            && !string.IsNullOrWhiteSpace(normalizedExpected)
            && string.Equals(normalizedActual, normalizedExpected, StringComparison.Ordinal);
    }

    private static string NormalizeResolvedCalculatorResult(string? text)
    {
        string normalized = NormalizeCalculatorDisplayText(text).Trim();
        return normalized.EndsWith('=') ? normalized[..^1] : normalized;
    }

    private static T Retry<T>(Func<T> action, Func<T, bool> predicate, string errorMessage, int attempts = 10, int delayMs = 350)
    {
        Exception? lastError = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                T result = action();
                if (predicate(result))
                    return result;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            Thread.Sleep(delayMs);
        }

        if (lastError is not null)
            throw new InvalidOperationException(errorMessage, lastError);

        throw new InvalidOperationException(errorMessage);
    }
}