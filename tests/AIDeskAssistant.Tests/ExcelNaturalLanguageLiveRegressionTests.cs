using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using AIDeskAssistant.Models;
using AIDeskAssistant.Platform.MacOS;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Tests;

public sealed class ExcelNaturalLanguageLiveRegressionTests
{
    private const string LiveLlmRegressionOptInVariable = "AIDESK_ENABLE_LIVE_EXCEL_LLM_REGRESSION_TESTS";

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task ExcelRuntimeLlmRegression_FillsColumnAOneToTenFromNaturalLanguageInstruction()
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
        string resultsDirectory = CreateLiveResultsDirectory("excel-llm-natural-language-column-a-1-to-10");
        var traceCapture = new LiveToolTraceCapture(resultsDirectory);

        QuitExcelIfRunning();

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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        string finalResponse = await assistant.SendMessageAsync(
            "Öffne Microsoft Excel auf macOS. Sorge dafür, dass ein bearbeitbares Arbeitsblatt im Vordergrund ist. Fülle in Spalte A die Zeilen 1 bis 10 mit den Werten 1 bis 10. Entscheide selbst, welche Zwischenschritte nötig sind, zum Beispiel ob du eine neue Arbeitsmappe brauchst, wie du das Raster fokussierst und wie du die Eingabe effizient machst. Verwende Excel selbst und nicht Terminal, Python, Shell-Kommandos oder externe Dateien. Bestätige den Erfolg erst, wenn A1 bis A10 in Excel sichtbar oder sonst konkret aus dem aktuellen Excel-Zustand verifiziert sind.",
            onToolCall: message =>
            {
                toolTrace.Add($"CALL {message}");
                traceCapture.HandleToolCall(message);
            },
            onToolResult: message =>
            {
                toolTrace.Add($"RESULT {message}");
                traceCapture.HandleToolResult(message);
            },
            maxToolRounds: 90,
            ct: cts.Token);

        await File.WriteAllLinesAsync(Path.Combine(resultsDirectory, "tool-trace.txt"), toolTrace, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "assistant-final-response.txt"), finalResponse, cts.Token);

        string toolTraceText = string.Join(Environment.NewLine, toolTrace);
        Assert.DoesNotContain("run_command(", toolTraceText, StringComparison.Ordinal);
        Assert.DoesNotContain("open_application({\"name\":\"Terminal\"})", toolTraceText, StringComparison.Ordinal);
        Assert.Contains(toolTrace, entry => entry.Contains("Excel", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(toolTrace, entry => entry.Contains("A1", StringComparison.OrdinalIgnoreCase) || entry.Contains("Spalte A", StringComparison.OrdinalIgnoreCase) || entry.Contains("column A", StringComparison.OrdinalIgnoreCase));

        _ = Retry(
            () =>
            {
                window.FocusWindow("Microsoft Excel", null);
                return window.GetFrontmostApplicationName();
            },
            appName => appName.Contains("Excel", StringComparison.OrdinalIgnoreCase),
            "Excel did not end up frontmost after the natural-language run.");

        WindowBounds activeWindowBounds = Retry(
            () => window.GetActiveWindowBounds(),
            bounds => bounds.Width > 0 && bounds.Height > 0,
            "Could not resolve Excel active window bounds after the natural-language run.");
        string finalUiSummary = Retry(
            uiAutomation.SummarizeFrontmostUiElements,
            summary => summary.Contains("Excel", StringComparison.OrdinalIgnoreCase) || summary.Contains("AXWindow", StringComparison.Ordinal),
            "Could not resolve Excel AX summary after the natural-language run.");
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "final-ui-summary.txt"), finalUiSummary, cts.Token);

        byte[] finalScreenshotBytes = CaptureScreenshotWithPython(activeWindowBounds);
        await File.WriteAllBytesAsync(Path.Combine(resultsDirectory, "final-window.png"), finalScreenshotBytes, cts.Token);

        string ocrText = textRecognition.RecognizeText(finalScreenshotBytes).FullText;
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "final-ocr.txt"), ocrText, cts.Token);

        IReadOnlyList<string> columnValues = ReadExcelColumnAValues(10);
        await File.WriteAllLinesAsync(Path.Combine(resultsDirectory, "final-column-a-values.txt"), columnValues, cts.Token);

        string[] expected = Enumerable.Range(1, 10).Select(static value => value.ToString()).ToArray();
        Assert.Equal(expected, columnValues);
    }

    private static string CreateLiveResultsDirectory(string folderName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestResults", folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    [SupportedOSPlatform("macos")]
    private static void QuitExcelIfRunning()
    {
        ProcessStartInfo startInfo = new("/usr/bin/osascript")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("tell application \"System Events\" to set excelRunning to exists process \"Microsoft Excel\"");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("if excelRunning then tell application \"Microsoft Excel\" to close every workbook saving no");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("if excelRunning then tell application \"Microsoft Excel\" to quit");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to quit Excel.");
        process.WaitForExit(15_000);
        Thread.Sleep(800);
    }

    [SupportedOSPlatform("macos")]
    private static byte[] CaptureScreenshotWithPython(WindowBounds bounds)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant-excel-natural-{Guid.NewGuid():N}.png");
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

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<string> ReadExcelColumnAValues(int rowCount)
    {
        ProcessStartInfo startInfo = new("/usr/bin/osascript")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string script = $$"""
        tell application "Microsoft Excel"
            if not (exists active workbook) then error "No active workbook available."
            set activeSheetRef to active sheet of active workbook
            set collectedValues to {}
            repeat with rowIndex from 1 to {{rowCount}}
                set cellValue to value of range ("A" & rowIndex) of activeSheetRef
                if cellValue is missing value then
                    set end of collectedValues to ""
                else
                    set end of collectedValues to (cellValue as string)
                end if
            end repeat
            set AppleScript's text item delimiters to linefeed
            return collectedValues as string
        end tell
        """;

        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start osascript for Excel verification.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Excel verification failed: {stderr.Trim()}");

        return stdout
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .Split('\n')
            .Select(static value => value.Trim())
            .ToArray();
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