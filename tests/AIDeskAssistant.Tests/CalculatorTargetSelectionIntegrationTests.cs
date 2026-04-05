using AIDeskAssistant.Models;
using AIDeskAssistant.Platform.MacOS;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace AIDeskAssistant.Tests;

internal sealed class CalculatorScreenshotService : IScreenshotService
{
    private readonly string _assetFileName;

    public CalculatorScreenshotService(string assetFileName)
    {
        _assetFileName = assetFileName;
    }

    public ScreenshotCaptureOptions LastOptions;

    public byte[] TakeScreenshot(ScreenshotCaptureOptions options = default)
    {
        LastOptions = options;
        return File.ReadAllBytes(GetAssetPath(_assetFileName));
    }

    public ScreenInfo GetScreenInfo() => new(1512, 982, 32);

    internal static string GetAssetPath(string assetFileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", assetFileName);
        Assert.True(File.Exists(path), $"Expected calculator screenshot asset at '{path}'.");
        return path;
    }
}

internal sealed class RuntimeCapturedScreenshotService : IScreenshotService
{
    private readonly byte[] _screenshotBytes;
    private readonly ScreenInfo _screenInfo;

    public RuntimeCapturedScreenshotService(byte[] screenshotBytes, ScreenInfo screenInfo)
    {
        _screenshotBytes = screenshotBytes;
        _screenInfo = screenInfo;
    }

    public ScreenshotCaptureOptions LastOptions;

    public byte[] TakeScreenshot(ScreenshotCaptureOptions options = default)
    {
        LastOptions = options;
        return _screenshotBytes;
    }

    public ScreenInfo GetScreenInfo() => _screenInfo;
}

public sealed class CalculatorTargetSelectionIntegrationTests
{
    private const string LiveRegressionOptInVariable = "AIDESK_ENABLE_LIVE_CALCULATOR_REGRESSION_TESTS";
    private const string LiveLlmRegressionOptInVariable = "AIDESK_ENABLE_LIVE_CALCULATOR_LLM_REGRESSION_TESTS";
    private const string NegativeSevenAsset = "calculator-negative-7-active-window.png";
    private const string ZeroAsset = "calculator-zero-active-window.png";
    private static readonly WindowBounds CalculatorCaptureBounds = new(612, 336, 262, 440);
    private static readonly WindowBounds CalculatorKey7Bounds = new(638, 539, 48, 48);
    private static readonly WindowBounds CalculatorPlusBounds = new(800, 647, 48, 48);
    private static readonly WindowBounds CalculatorEqualsBounds = new(800, 701, 48, 48);

    public static IEnumerable<object[]> CalculatorAdditionSequenceTargets()
    {
        yield return [new CalculatorTargetSpec("calculator_key=7", "press calculator key 7", 662, 563, new WindowBounds(638, 539, 48, 48), "Drücke im Rechner zuerst die Taste 7.")];
        yield return [new CalculatorTargetSpec("calculator_key=plus", "press calculator plus button", 824, 671, CalculatorPlusBounds, "Drücke im Rechner danach die Plus-Taste.")];
        yield return [new CalculatorTargetSpec("calculator_key=equals", "press calculator equals button", 824, 725, CalculatorEqualsBounds, "Drücke im Rechner zum Schluss die Gleich-Taste.")];
    }

    public static IEnumerable<object[]> CalculatorAdditionSequenceVisualizations()
    {
        yield return [new CalculatorVisualizationSpec("calculator-key-7-plus-sequence-step-1.png", ZeroAsset, 676, 492, 662, 563, new WindowBounds(638, 539, 48, 48), "calculator_key=7")];
        yield return [new CalculatorVisualizationSpec("calculator-key-7-plus-sequence-step-2.png", ZeroAsset, 676, 492, 824, 671, CalculatorPlusBounds, "calculator_key=plus")];
        yield return [new CalculatorVisualizationSpec("calculator-key-7-plus-sequence-step-3.png", ZeroAsset, 676, 492, 824, 725, CalculatorEqualsBounds, "calculator_key=equals")];
    }

    public readonly record struct CalculatorTargetSpec(
        string TargetLabel,
        string PredictedAction,
        int ClickX,
        int ClickY,
        WindowBounds ElementBounds,
        string UserTask);

    public readonly record struct CalculatorVisualizationSpec(
        string ResultFileName,
        string AssetFileName,
        int CursorX,
        int CursorY,
        int ClickX,
        int ClickY,
        WindowBounds ElementBounds,
        string TargetLabel);

    private readonly record struct RuntimeCalculatorButtonTarget(string Label, WindowBounds Bounds)
    {
        public int CenterX => Bounds.X + (Bounds.Width / 2);
        public int CenterY => Bounds.Y + (Bounds.Height / 2);
    }

    private readonly record struct LiveCalculatorStep(string TargetKey, string PredictedAction, string ExpectedDisplayAfterClick);

    private sealed record LlmPreparedStepAssessment(string TargetLabel, bool ShouldClick, string Reason);

    private sealed record LlmAfterStepAssessment(bool StateConfirmed, string VisibleState, string Reason);

    private readonly record struct CalculatorOcrCandidate(string Text, double Confidence, WindowBounds? Bounds);

    [Fact]
    [SupportedOSPlatform("macos")]
    public void CalculatorRuntimeScreenshotIntegration_BuildsClickPredictionFromPythonCapturedActiveWindow()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (!string.Equals(Environment.GetEnvironmentVariable(LiveRegressionOptInVariable), "1", StringComparison.Ordinal))
            return;

        var window = new MacOSWindowService();
        var uiAutomation = new MacOSUiAutomationService();
        var screenInfoProvider = new MacOSScreenshotService();

        EnsureCalculatorIsReady(window);
        WindowBounds activeWindowBounds = Retry(
            () => window.GetActiveWindowBounds(),
            bounds => bounds.Width > 0 && bounds.Height > 0,
            "Could not resolve Calculator active window bounds.");
        string uiSummary = Retry(
            uiAutomation.SummarizeFrontmostUiElements,
            summary => summary.Contains("calculator_key=7", StringComparison.Ordinal),
            "Could not resolve calculator AX summary with calculator_key=7.");
        RuntimeCalculatorButtonTarget key7Target = ParseCalculatorButtonTarget(uiSummary, "7");
        byte[] runtimeScreenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);

        var screenshot = new RuntimeCapturedScreenshotService(runtimeScreenshotBytes, screenInfoProvider.GetScreenInfo());
        var mouse = new FakeMouseService();
        var keyboard = new FakeKeyboardService();
        var terminal = new FakeTerminalService();
        var textRecognition = new FakeTextRecognitionService();
        var executor = new DesktopToolExecutor(screenshot, mouse, keyboard, terminal, window, uiAutomation, textRecognition);

        string screenshotResult = executor.Execute(
            "take_screenshot",
            $"{{\"target\":\"active_window\",\"purpose\":\"validate live calculator target from runtime screenshot\",\"padding\":0,\"predicted_tool\":\"click\",\"predicted_action\":\"press calculator key 7\",\"predicted_target_label\":\"calculator_key=7\",\"intended_element_x\":{key7Target.Bounds.X},\"intended_element_y\":{key7Target.Bounds.Y},\"intended_element_width\":{key7Target.Bounds.Width},\"intended_element_height\":{key7Target.Bounds.Height},\"intended_element_label\":\"{key7Target.Label}\"}}");

        bool parsed = AIService.TryParseScreenshotAttachment(screenshotResult, out ScreenshotModelAttachment? attachment);

        Assert.True(parsed);
        Assert.NotNull(attachment);
        Assert.NotEmpty(attachment.Bytes);
        Assert.Equal(activeWindowBounds, screenshot.LastOptions.Bounds);
        Assert.Contains("Predicted next tool: click.", screenshotResult);
        Assert.Contains("Predicted next action: press calculator key 7.", screenshotResult);
        Assert.Contains("Predicted target/button: calculator_key=7.", screenshotResult);
        Assert.Contains($"Highlighted AX element: X={key7Target.Bounds.X}, Y={key7Target.Bounds.Y}, Width={key7Target.Bounds.Width}, Height={key7Target.Bounds.Height}, IntersectsCapture=True, Label={key7Target.Label}.", screenshotResult);
        Assert.Contains($"AX-derived click target: X={key7Target.CenterX}, Y={key7Target.CenterY}, InsideCapture=True, Label={key7Target.Label}.", screenshotResult);

        ScreenshotSupplementalImage schematicTargetImage = Assert.Single(attachment.SupplementalImages);
        Assert.Equal("schematic-target", schematicTargetImage.Label);
        Assert.Equal("image/png", schematicTargetImage.MediaType);
        Assert.NotEmpty(schematicTargetImage.Bytes);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task CalculatorRuntimeLlmRegression_ExecutesSevenPlusSevenEqualsWithBeforePreparedAndAfterScreens()
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
        var chatClient = new ChatClient(model, apiKey);
        var window = new MacOSWindowService();
        var uiAutomation = new MacOSUiAutomationService();
        var screenInfoProvider = new MacOSScreenshotService();
        var ocr = new MacOSVisionTextRecognitionService();
        string resultsDirectory = CreateLiveResultsDirectory("calculator-llm-regression");

        EnsureCalculatorIsReady(window);
        await ResetCalculatorToZeroAsync(window, uiAutomation, resultsDirectory, ocr);

        WindowBounds activeWindowBounds = GetLiveCalculatorWindowBounds(window);
        byte[] initialScreenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);
        var screenshot = new MutableRuntimeCapturedScreenshotService(initialScreenshotBytes, screenInfoProvider.GetScreenInfo());
        var mouse = new MacOSMouseService();
        var keyboard = new FakeKeyboardService();
        var terminal = new FakeTerminalService();
        var executor = new DesktopToolExecutor(screenshot, mouse, keyboard, terminal, window, uiAutomation, ocr);

        LiveCalculatorStep[] steps =
        [
            new("7", "press calculator key 7", "7"),
            new("plus", "press calculator plus button", "7+"),
            new("7", "press calculator key 7", "7+7"),
            new("equals", "press calculator equals button", "14"),
        ];

        for (int index = 0; index < steps.Length; index++)
        {
            LiveCalculatorStep step = steps[index];
            activeWindowBounds = GetLiveCalculatorWindowBounds(window);
            string uiSummary = Retry(
                uiAutomation.SummarizeFrontmostUiElements,
                summary => summary.Contains("AXButton", StringComparison.Ordinal),
                "Could not resolve calculator AX summary with visible buttons.");
            RuntimeCalculatorButtonTarget target = ResolveLiveCalculatorButtonTarget(uiSummary, step.TargetKey);
            string stepPrefix = $"{index + 1:D2}-{step.TargetKey}";
            await File.WriteAllTextAsync(Path.Combine(resultsDirectory, $"{stepPrefix}-ui-summary.txt"), uiSummary);
            await File.WriteAllTextAsync(
                Path.Combine(resultsDirectory, $"{stepPrefix}-resolved-target.json"),
                JsonSerializer.Serialize(new
                {
                    step.TargetKey,
                    target.Label,
                    target.Bounds,
                    target.CenterX,
                    target.CenterY,
                }, new JsonSerializerOptions { WriteIndented = true }));

            ParkMouseAwayFromWindow(activeWindowBounds, mouse);
            await Task.Delay(700);

            byte[] beforeScreenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);
            screenshot.SetScreenshotBytes(beforeScreenshotBytes);
            string preparedScreenshotResult = executor.Execute(
                "take_screenshot",
                $"{{\"target\":\"active_window\",\"purpose\":\"prepare calculator step {index + 1} for 7+7=\",\"padding\":0,\"predicted_tool\":\"click\",\"predicted_action\":\"{step.PredictedAction}\",\"predicted_target_label\":\"calculator_key={step.TargetKey}\",\"intended_element_x\":{target.Bounds.X},\"intended_element_y\":{target.Bounds.Y},\"intended_element_width\":{target.Bounds.Width},\"intended_element_height\":{target.Bounds.Height},\"intended_element_label\":\"{target.Label}\"}}");

            bool parsedPrepared = AIService.TryParseScreenshotAttachment(preparedScreenshotResult, out ScreenshotModelAttachment? preparedAttachment);
            Assert.True(parsedPrepared);
            Assert.NotNull(preparedAttachment);

            SaveStepImage(resultsDirectory, $"{stepPrefix}-before.png", preparedAttachment.Bytes);
            ScreenshotSupplementalImage? preparedImage = preparedAttachment.SupplementalImages.SingleOrDefault();
            byte[] preparedImageBytes = preparedImage is not null && preparedImage.Bytes.Length > 0
                ? preparedImage.Bytes
                : BuildPreparedCalculatorTargetImage(beforeScreenshotBytes, activeWindowBounds, target);
            string preparedImageMediaType = preparedImage is null || string.IsNullOrWhiteSpace(preparedImage.MediaType) ? "image/png" : preparedImage.MediaType;
            SaveStepImage(resultsDirectory, $"{stepPrefix}-prepared.png", preparedImageBytes);

            LlmPreparedStepAssessment preparedAssessment = await AssessPreparedStepWithLlmAsync(
                chatClient,
                uiSummary,
                preparedAttachment,
                preparedImageBytes,
                preparedImageMediaType,
                step);
            await File.WriteAllTextAsync(Path.Combine(resultsDirectory, $"{stepPrefix}-prepared-analysis.json"), JsonSerializer.Serialize(preparedAssessment, new JsonSerializerOptions { WriteIndented = true }));

            Assert.True(preparedAssessment.ShouldClick, $"LLM did not approve step {index + 1}: {preparedAssessment.Reason}");
            Assert.Equal(target.Label, preparedAssessment.TargetLabel);

            string clickResult = executor.Execute(
                "click",
                $"{{\"intended_element_x\":{target.Bounds.X},\"intended_element_y\":{target.Bounds.Y},\"intended_element_width\":{target.Bounds.Width},\"intended_element_height\":{target.Bounds.Height},\"intended_element_label\":\"{target.Label}\"}}");
            Assert.Contains($"({target.CenterX}, {target.CenterY})", clickResult);

            await Task.Delay(250);
            ParkMouseAwayFromWindow(activeWindowBounds, mouse);
            await Task.Delay(700);

            byte[] afterScreenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);
            screenshot.SetScreenshotBytes(afterScreenshotBytes);
            string afterScreenshotResult = executor.Execute(
                "take_screenshot",
                $"{{\"target\":\"active_window\",\"purpose\":\"validate calculator state after step {index + 1}\",\"padding\":0}}");
            bool parsedAfter = AIService.TryParseScreenshotAttachment(afterScreenshotResult, out ScreenshotModelAttachment? afterAttachment);
            Assert.True(parsedAfter);
            Assert.NotNull(afterAttachment);
            SaveStepImage(resultsDirectory, $"{stepPrefix}-after.png", afterAttachment.Bytes);

            string afterUiSummary = Retry(
                uiAutomation.SummarizeFrontmostUiElements,
                summary => summary.Contains("AXButton", StringComparison.Ordinal),
                "Could not resolve calculator AX summary after click.");
            await File.WriteAllTextAsync(Path.Combine(resultsDirectory, $"{stepPrefix}-after-ui-summary.txt"), afterUiSummary);
            string afterOcrText = RecognizeCalculatorDisplayText(afterAttachment.Bytes, activeWindowBounds, ocr, afterUiSummary);
            LlmAfterStepAssessment afterAssessment = await AssessAfterStepWithLlmAsync(
                chatClient,
                afterUiSummary,
                afterAttachment,
                step,
                afterOcrText);
            await File.WriteAllTextAsync(Path.Combine(resultsDirectory, $"{stepPrefix}-after-analysis.json"), JsonSerializer.Serialize(afterAssessment, new JsonSerializerOptions { WriteIndented = true }));

            Assert.True(afterAssessment.StateConfirmed, $"LLM did not confirm post-click state for step {index + 1}: {afterAssessment.Reason}");
            string normalizedExpectedDisplay = NormalizeCalculatorDisplayText(step.ExpectedDisplayAfterClick);
            Assert.True(
                afterOcrText.Contains(normalizedExpectedDisplay, StringComparison.Ordinal)
                    || string.Equals(NormalizeCalculatorDisplayText(afterAssessment.VisibleState), normalizedExpectedDisplay, StringComparison.Ordinal),
                $"Expected calculator display '{normalizedExpectedDisplay}' after step {index + 1}, but OCR was '{afterOcrText}' and LLM reported '{afterAssessment.VisibleState}'.");
        }

        byte[] finalScreenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);
        string finalUiSummary = Retry(
            uiAutomation.SummarizeFrontmostUiElements,
            summary => summary.Contains("AXButton", StringComparison.Ordinal),
            "Could not resolve calculator AX summary for final state.");
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "final-ui-summary.txt"), finalUiSummary);
        string finalOcrText = RecognizeCalculatorDisplayText(finalScreenshotBytes, activeWindowBounds, ocr, finalUiSummary);
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "final-ocr.txt"), finalOcrText);
        Assert.Contains("14", finalOcrText);
    }

    [Fact]
    public void CalculatorKeySelectionContext_PreservesScreenshotAndAxSignalsForKey7()
    {
        var screenshot = new CalculatorScreenshotService(NegativeSevenAsset);
        var mouse = new FakeMouseService();
        var keyboard = new FakeKeyboardService();
        var terminal = new FakeTerminalService();
        var window = new FakeWindowService
        {
            Bounds = new WindowBounds(628, 352, 230, 408),
            WindowAtPoint = new WindowHitTestResult("Calculator", "Rechner", new WindowBounds(628, 352, 230, 408)),
            FrontmostApplicationName = "Calculator",
        };
        var uiAutomation = new FakeUiAutomationService
        {
            FrontmostUiSummary = "Frontmost app: Rechner\nFocused window: Rechner at x=628,y=352,w=230,h=408\nVisible UI elements:\n- AXButton | calculator_key=7 | x=638,y=539,w=48,h=48\n- AXButton | calculator_key=2 | x=694,y=647,w=48,h=48\n- AXButton | calculator_key=plus_minus | x=638,y=701,w=48,h=48\n- AXButton | title=Delete | x=639,y=485,w=48,h=48"
        };
        var textRecognition = new FakeTextRecognitionService();
        var executor = new DesktopToolExecutor(screenshot, mouse, keyboard, terminal, window, uiAutomation, textRecognition);

        string uiSummary = executor.Execute("get_frontmost_ui_elements", "{}");
        string screenshotResult = executor.Execute(
            "take_screenshot",
            "{\"target\":\"active_window\",\"purpose\":\"validate calculator target before pressing the key\",\"visual_style\":\"schematic_target\",\"predicted_tool\":\"click\",\"predicted_action\":\"press calculator key 7\",\"predicted_target_label\":\"calculator_key=7\",\"intended_click_x\":662,\"intended_click_y\":563,\"intended_click_label\":\"calculator_key=7\",\"intended_element_x\":638,\"intended_element_y\":539,\"intended_element_width\":48,\"intended_element_height\":48,\"intended_element_label\":\"calculator_key=7\"}");

        string compactedScreenshot = AIService.CompactToolResultForRealtimeTransport("take_screenshot", screenshotResult);
        string userMessage = AIService.BuildUserMessageWithScreenInfo(
            "Drücke im Rechner nur die Taste 7.",
            "Screen: 1512x982, 32 bpp",
            uiSummary);

        bool parsed = AIService.TryParseScreenshotAttachment(screenshotResult, out ScreenshotModelAttachment? attachment);

        Assert.True(parsed);
        Assert.NotNull(attachment);
        Assert.NotEmpty(attachment.Bytes);

        Assert.Equal(CalculatorCaptureBounds, screenshot.LastOptions.Bounds);

        Assert.Contains("calculator_key=7", uiSummary);
        Assert.Contains("calculator_key=2", uiSummary);
        Assert.Contains("calculator_key=plus_minus", uiSummary);

        Assert.Contains("Visual style: schematic_target.", compactedScreenshot);
        Assert.Contains("Predicted next tool: click.", compactedScreenshot);
        Assert.Contains("Predicted next action: press calculator key 7.", compactedScreenshot);
        Assert.Contains("Predicted target/button: calculator_key=7.", compactedScreenshot);
        Assert.Contains("Highlighted AX element: X=638, Y=539, Width=48, Height=48, IntersectsCapture=True, Label=calculator_key=7.", compactedScreenshot);
        Assert.Contains("AX-derived click target: X=662, Y=563, InsideCapture=True, Label=calculator_key=7.", compactedScreenshot);
        Assert.Contains("Schematic target overview:", compactedScreenshot);
        Assert.DoesNotContain("Predicted target/button: calculator_key=2.", compactedScreenshot);

        Assert.Contains("Current macOS Accessibility UI summary:", userMessage);
        Assert.Contains("calculator_key=7", userMessage);
        Assert.Contains("calculator_key=2", userMessage);
        Assert.Contains("Drücke im Rechner nur die Taste 7.", userMessage);
    }

    [Theory]
    [MemberData(nameof(CalculatorAdditionSequenceTargets))]
    public void CalculatorAdditionSequenceContext_PreservesSignalsForExpectedTarget(CalculatorTargetSpec target)
    {
        var screenshot = new CalculatorScreenshotService(ZeroAsset);
        var mouse = new FakeMouseService();
        var keyboard = new FakeKeyboardService();
        var terminal = new FakeTerminalService();
        var window = new FakeWindowService
        {
            Bounds = new WindowBounds(628, 352, 230, 408),
            WindowAtPoint = new WindowHitTestResult("Calculator", "Rechner", new WindowBounds(628, 352, 230, 408)),
            FrontmostApplicationName = "Calculator",
        };
        var uiAutomation = new FakeUiAutomationService
        {
            FrontmostUiSummary = "Frontmost app: Rechner\nFocused window: Rechner at x=628,y=352,w=230,h=408\nVisible UI elements:\n- AXButton | calculator_key=7 | x=638,y=539,w=48,h=48\n- AXButton | calculator_key=8 | x=694,y=539,w=48,h=48\n- AXButton | calculator_key=plus | x=800,y=647,w=48,h=48\n- AXButton | calculator_key=equals | x=800,y=701,w=48,h=48\n- AXButton | calculator_key=divide | x=800,y=485,w=48,h=48"
        };
        var textRecognition = new FakeTextRecognitionService();
        var executor = new DesktopToolExecutor(screenshot, mouse, keyboard, terminal, window, uiAutomation, textRecognition);

        string uiSummary = executor.Execute("get_frontmost_ui_elements", "{}");
        string screenshotResult = executor.Execute(
            "take_screenshot",
            $"{{\"target\":\"active_window\",\"purpose\":\"validate calculator target for 7 plus 7 equals\",\"visual_style\":\"schematic_target\",\"predicted_tool\":\"click\",\"predicted_action\":\"{target.PredictedAction}\",\"predicted_target_label\":\"{target.TargetLabel}\",\"intended_click_x\":{target.ClickX},\"intended_click_y\":{target.ClickY},\"intended_click_label\":\"{target.TargetLabel}\",\"intended_element_x\":{target.ElementBounds.X},\"intended_element_y\":{target.ElementBounds.Y},\"intended_element_width\":{target.ElementBounds.Width},\"intended_element_height\":{target.ElementBounds.Height},\"intended_element_label\":\"{target.TargetLabel}\"}}");

        string compactedScreenshot = AIService.CompactToolResultForRealtimeTransport("take_screenshot", screenshotResult);
        string userMessage = AIService.BuildUserMessageWithScreenInfo(target.UserTask, "Screen: 1512x982, 32 bpp", uiSummary);

        Assert.Equal(CalculatorCaptureBounds, screenshot.LastOptions.Bounds);
        Assert.Contains(target.TargetLabel, uiSummary);
        Assert.Contains("calculator_key=7", uiSummary);
        Assert.Contains("calculator_key=plus", uiSummary);
        Assert.Contains("calculator_key=equals", uiSummary);

        Assert.Contains($"Predicted next action: {target.PredictedAction}.", compactedScreenshot);
        Assert.Contains($"Predicted target/button: {target.TargetLabel}.", compactedScreenshot);
        Assert.Contains($"Highlighted AX element: X={target.ElementBounds.X}, Y={target.ElementBounds.Y}, Width={target.ElementBounds.Width}, Height={target.ElementBounds.Height}, IntersectsCapture=True, Label={target.TargetLabel}.", compactedScreenshot);
        Assert.Contains($"AX-derived click target: X={target.ClickX}, Y={target.ClickY}, InsideCapture=True, Label={target.TargetLabel}.", compactedScreenshot);
        Assert.Contains("Schematic target overview:", compactedScreenshot);
        Assert.Contains(target.TargetLabel, userMessage);
        Assert.Contains(target.UserTask, userMessage);
    }

    [Fact]
    public void CalculatorKeySelectionVisualization_WritesAnnotatedTargetResultImage()
    {
        byte[] sourceBytes = File.ReadAllBytes(CalculatorScreenshotService.GetAssetPath(NegativeSevenAsset));
        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(
            sourceBytes,
            new ScreenshotAnnotationData(
                CalculatorCaptureBounds,
                836,
                797,
                ScreenshotVisualStyles.SchematicTarget,
                IntendedClickTarget: new ScreenshotClickTarget(662, 563, "calculator_key=7"),
                IntendedElementRegion: new ScreenshotHighlightedRegion(CalculatorKey7Bounds, "calculator_key=7")));

        string resultsDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDirectory);
        string resultPath = Path.Combine(resultsDirectory, "calculator-key-7-target-result.png");
        File.WriteAllBytes(resultPath, annotatedBytes);

        using var bitmap = SkiaSharp.SKBitmap.Decode(annotatedBytes);

        Assert.True(File.Exists(resultPath));
        Assert.NotNull(bitmap);
        Assert.Equal(CalculatorCaptureBounds.Width * 2, bitmap.Width);
        Assert.Equal(CalculatorCaptureBounds.Height * 2, bitmap.Height);
    }

    [Theory]
    [MemberData(nameof(CalculatorAdditionSequenceVisualizations))]
    public void CalculatorAdditionSequenceVisualization_WritesAnnotatedTargetResultImage(CalculatorVisualizationSpec visualization)
    {
        byte[] sourceBytes = File.ReadAllBytes(CalculatorScreenshotService.GetAssetPath(visualization.AssetFileName));
        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(
            sourceBytes,
            new ScreenshotAnnotationData(
                CalculatorCaptureBounds,
                visualization.CursorX,
                visualization.CursorY,
                ScreenshotVisualStyles.SchematicTarget,
                IntendedClickTarget: new ScreenshotClickTarget(visualization.ClickX, visualization.ClickY, visualization.TargetLabel),
                IntendedElementRegion: new ScreenshotHighlightedRegion(visualization.ElementBounds, visualization.TargetLabel)));

        string resultsDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDirectory);
        string resultPath = Path.Combine(resultsDirectory, visualization.ResultFileName);
        File.WriteAllBytes(resultPath, annotatedBytes);

        using var bitmap = SkiaSharp.SKBitmap.Decode(annotatedBytes);

        Assert.True(File.Exists(resultPath));
        Assert.NotNull(bitmap);
        Assert.Equal(CalculatorCaptureBounds.Width * 2, bitmap.Width);
        Assert.Equal(CalculatorCaptureBounds.Height * 2, bitmap.Height);
    }

    [SupportedOSPlatform("macos")]
    private static void EnsureCalculatorIsReady(MacOSWindowService window)
    {
        ProcessStartInfo startInfo = new("/usr/bin/open")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add("Calculator");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch Calculator.");
        process.WaitForExit(10_000);

        Retry(
            () =>
            {
                _ = window.FocusWindow("Calculator", null);
                return window.GetFrontmostApplicationName();
            },
            appName => appName.Contains("Calculator", StringComparison.OrdinalIgnoreCase)
                || appName.Contains("Rechner", StringComparison.OrdinalIgnoreCase),
            "Calculator did not become the frontmost application.");

        window.ResizeActiveWindow(230, 408);
        Thread.Sleep(600);
    }

    private static RuntimeCalculatorButtonTarget ParseCalculatorButtonTarget(string uiSummary, string calculatorKey)
    {
        Match match = Regex.Match(
            uiSummary,
            $@"calculator_key={Regex.Escape(calculatorKey)}\s*\|\s*x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success, $"Expected calculator_key={calculatorKey} in AX summary. Summary:{Environment.NewLine}{uiSummary}");

        WindowBounds bounds = new(
            int.Parse(match.Groups["x"].Value),
            int.Parse(match.Groups["y"].Value),
            int.Parse(match.Groups["w"].Value),
            int.Parse(match.Groups["h"].Value));
        return new RuntimeCalculatorButtonTarget($"calculator_key={calculatorKey}", bounds);
    }

    private static RuntimeCalculatorButtonTarget? TryParseCalculatorButtonTarget(string uiSummary, string calculatorKey)
    {
        Match match = Regex.Match(
            uiSummary,
            $@"calculator_key={Regex.Escape(calculatorKey)}\s*\|\s*x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        WindowBounds bounds = new(
            int.Parse(match.Groups["x"].Value),
            int.Parse(match.Groups["y"].Value),
            int.Parse(match.Groups["w"].Value),
            int.Parse(match.Groups["h"].Value));
        return new RuntimeCalculatorButtonTarget($"calculator_key={calculatorKey}", bounds);
    }

    private static RuntimeCalculatorButtonTarget ResolveLiveCalculatorButtonTarget(string uiSummary, string calculatorKey)
        => TryResolveLiveCalculatorButtonTarget(uiSummary, calculatorKey)
            ?? throw new Xunit.Sdk.XunitException($"Expected live calculator button '{calculatorKey}' in AX summary.{Environment.NewLine}{uiSummary}");

    private static RuntimeCalculatorButtonTarget? TryResolveLiveCalculatorButtonTarget(string uiSummary, string calculatorKey)
    {
        IReadOnlyList<RuntimeCalculatorButtonTarget> mappedButtons = ParseCalculatorButtonsFromGrid(uiSummary);
        RuntimeCalculatorButtonTarget? gridMatch = mappedButtons
            .Where(target => string.Equals(target.Label, $"calculator_key={calculatorKey}", StringComparison.Ordinal))
            .Select(static target => (RuntimeCalculatorButtonTarget?)target)
            .FirstOrDefault();
        if (gridMatch.HasValue)
            return gridMatch.Value;

        return TryParseCalculatorButtonTarget(uiSummary, calculatorKey);
    }

    private static IReadOnlyList<RuntimeCalculatorButtonTarget> ParseCalculatorButtonsFromGrid(string uiSummary)
    {
        MatchCollection matches = Regex.Matches(
            uiSummary,
            @"AXButton\b.*?x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        var buttons = matches.Cast<Match>()
            .Where(static match => match.Success)
            .Select(static match => new WindowBounds(
                int.Parse(match.Groups["x"].Value),
                int.Parse(match.Groups["y"].Value),
                int.Parse(match.Groups["w"].Value),
                int.Parse(match.Groups["h"].Value)))
            .Where(static bounds => bounds.Width >= 40 && bounds.Height >= 40)
            .OrderBy(static bounds => bounds.Y)
            .ThenBy(static bounds => bounds.X)
            .ToList();

        if (buttons.Count == 0)
            return Array.Empty<RuntimeCalculatorButtonTarget>();

        var rows = new List<List<WindowBounds>>();
        foreach (WindowBounds button in buttons)
        {
            List<WindowBounds>? existingRow = rows.FirstOrDefault(row => Math.Abs(row[0].Y - button.Y) <= 8);
            if (existingRow is null)
            {
                rows.Add([button]);
                continue;
            }

            existingRow.Add(button);
        }

        string[][] rowLabels =
        [
            ["delete", "ac", "percent", "divide"],
            ["7", "8", "9", "multiply"],
            ["4", "5", "6", "minus"],
            ["1", "2", "3", "plus"],
            ["plus_minus", "0", "decimal", "equals"],
        ];

        List<List<WindowBounds>> keypadRows = rows
            .Where(static row => row.Count >= 4)
            .OrderBy(static row => row[0].Y)
            .Skip(Math.Max(0, rows.Count(static row => row.Count >= 4) - 5))
            .Select(static row => row.OrderBy(bounds => bounds.X).Take(4).ToList())
            .ToList();

        if (keypadRows.Count < 5)
            return Array.Empty<RuntimeCalculatorButtonTarget>();

        var targets = new List<RuntimeCalculatorButtonTarget>();
        for (int rowIndex = 0; rowIndex < 5; rowIndex++)
        {
            for (int columnIndex = 0; columnIndex < 4; columnIndex++)
            {
                targets.Add(new RuntimeCalculatorButtonTarget(
                    $"calculator_key={rowLabels[rowIndex][columnIndex]}",
                    keypadRows[rowIndex][columnIndex]));
            }
        }

        return targets;
    }

    [SupportedOSPlatform("macos")]
    private static byte[] CaptureActiveWindowScreenshotWithPython(WindowBounds bounds)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant-calculator-runtime-{Guid.NewGuid():N}.png");
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

            Assert.True(File.Exists(outputPath), $"Expected Python runtime screenshot at '{outputPath}'.");
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [SupportedOSPlatform("macos")]
    private static WindowBounds GetLiveCalculatorWindowBounds(MacOSWindowService window)
        => Retry(
            () => window.GetActiveWindowBounds(),
            bounds => bounds.Width > 0 && bounds.Height > 0,
            "Could not resolve Calculator active window bounds.");

    [SupportedOSPlatform("macos")]
    private static void ParkMouseAwayFromWindow(WindowBounds bounds, IMouseService mouse)
    {
        mouse.MoveTo(bounds.X + bounds.Width + 60, bounds.Y + 40);
        mouse.MoveTo(bounds.X + bounds.Width + 90, bounds.Y + bounds.Height + 40);
    }

    [SupportedOSPlatform("macos")]
    private static async Task ResetCalculatorToZeroAsync(MacOSWindowService window, MacOSUiAutomationService uiAutomation, string resultsDirectory, ITextRecognitionService ocr)
    {
        var mouse = new MacOSMouseService();
        WindowBounds activeWindowBounds = GetLiveCalculatorWindowBounds(window);

        for (int attempt = 0; attempt < 8; attempt++)
        {
            byte[] screenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);
            string normalizedText = RecognizeCalculatorDisplayText(screenshotBytes, activeWindowBounds, ocr);
            await File.WriteAllTextAsync(Path.Combine(resultsDirectory, $"reset-{attempt + 1}-ocr.txt"), normalizedText);
            if (normalizedText == "0")
                return;

            string uiSummary = uiAutomation.SummarizeFrontmostUiElements();
            RuntimeCalculatorButtonTarget? resetTarget = TryResolveLiveCalculatorButtonTarget(uiSummary, "delete")
                ?? TryResolveLiveCalculatorButtonTarget(uiSummary, "ac");
            Assert.True(resetTarget.HasValue, $"Could not find a reset button in Calculator AX summary.{Environment.NewLine}{uiSummary}");

            mouse.ClickAt(resetTarget.Value.CenterX, resetTarget.Value.CenterY);
            await Task.Delay(300);
        }

        byte[] finalScreenshotBytes = CaptureActiveWindowScreenshotWithPython(activeWindowBounds);
        string finalText = RecognizeCalculatorDisplayText(finalScreenshotBytes, activeWindowBounds, ocr);
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "reset-final-ocr.txt"), finalText);
        if (finalText == "0")
            return;

        QuitCalculatorIfRunning();
        EnsureCalculatorIsReady(window);
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
    }

    private static string CreateLiveResultsDirectory(string folderName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestResults", folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SaveStepImage(string resultsDirectory, string fileName, byte[] bytes)
        => File.WriteAllBytes(Path.Combine(resultsDirectory, fileName), bytes);

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

        normalized = Regex.Replace(normalized, @"[^0-9+*/=.-]", string.Empty, RegexOptions.CultureInvariant);
        return normalized;
    }

    private static string RecognizeCalculatorDisplayText(byte[] screenshotBytes, WindowBounds captureBounds, ITextRecognitionService ocr, string? uiSummary = null)
    {
        string axDisplayText = NormalizeCalculatorDisplayText(TryGetCalculatorDisplayTextFromUiSummary(uiSummary));
        if (IsPlausibleCalculatorDisplayText(axDisplayText))
            return axDisplayText;

        if (TryGetCalculatorDisplayRegionFromUiSummary(uiSummary, out WindowBounds displayRegionFromUi))
        {
            byte[] croppedBytes = CropScreenshotToWindowBoundsRegion(screenshotBytes, captureBounds, displayRegionFromUi);
            string recognizedText = RecognizeBestCalculatorDisplayText(croppedBytes, ocr);
            if (!string.IsNullOrWhiteSpace(recognizedText))
                return recognizedText;
        }

        foreach (WindowBounds displayRegion in GetCalculatorDisplayRegions(captureBounds))
        {
            byte[] croppedBytes = CropScreenshotToWindowBoundsRegion(screenshotBytes, captureBounds, displayRegion);
            string recognizedText = RecognizeBestCalculatorDisplayText(croppedBytes, ocr);
            if (!string.IsNullOrWhiteSpace(recognizedText))
                return recognizedText;
        }

        return RecognizeBestCalculatorDisplayText(screenshotBytes, ocr);
    }

    private static string? TryGetCalculatorDisplayTextFromUiSummary(string? uiSummary)
    {
        if (string.IsNullOrWhiteSpace(uiSummary))
            return null;

        MatchCollection staticTextMatches = Regex.Matches(
            uiSummary,
            @"AXStaticText\s*\|\s*value=(?<value>.*?)\s*\|\s*x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        List<CalculatorOcrCandidate> candidates = staticTextMatches
            .Cast<Match>()
            .Where(static match => match.Success)
            .Select(static match => new CalculatorOcrCandidate(
                NormalizeCalculatorDisplayText(match.Groups["value"].Value),
                1,
                new WindowBounds(
                    int.Parse(match.Groups["x"].Value),
                    int.Parse(match.Groups["y"].Value),
                    int.Parse(match.Groups["w"].Value),
                    int.Parse(match.Groups["h"].Value))))
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .ToList();

        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(ScoreCalculatorDisplayCandidate)
            .Select(static candidate => candidate.Text)
            .First();
    }

    private static string RecognizeBestCalculatorDisplayText(byte[] imageBytes, ITextRecognitionService ocr)
    {
        TextRecognitionResult originalResult = ocr.RecognizeText(imageBytes);
        string bestOriginal = SelectBestCalculatorDisplayText(originalResult);
        if (IsPlausibleCalculatorDisplayText(bestOriginal))
            return bestOriginal;

        byte[] preprocessedBytes = PreprocessCalculatorDisplayImage(imageBytes);
        TextRecognitionResult preprocessedResult = ocr.RecognizeText(preprocessedBytes);
        string bestPreprocessed = SelectBestCalculatorDisplayText(preprocessedResult);
        if (IsPlausibleCalculatorDisplayText(bestPreprocessed))
            return bestPreprocessed;

        return bestPreprocessed.Length > bestOriginal.Length ? bestPreprocessed : bestOriginal;
    }

    private static string SelectBestCalculatorDisplayText(TextRecognitionResult result)
    {
        List<CalculatorOcrCandidate> candidates = result.Lines
            .Select(static line => new CalculatorOcrCandidate(NormalizeCalculatorDisplayText(line.Text), line.Confidence, line.Bounds))
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Text))
            .ToList();

        string normalizedFullText = NormalizeCalculatorDisplayText(result.FullText);
        if (!string.IsNullOrWhiteSpace(normalizedFullText))
            candidates.Add(new CalculatorOcrCandidate(normalizedFullText, 0, null));

        if (candidates.Count == 0)
            return string.Empty;

        IEnumerable<CalculatorOcrCandidate> preferredCandidates = candidates.Where(static candidate => IsPlausibleCalculatorDisplayText(candidate.Text));
        IEnumerable<CalculatorOcrCandidate> pool = preferredCandidates.Any() ? preferredCandidates : candidates;

        return pool
            .OrderByDescending(ScoreCalculatorOcrCandidate)
            .Select(static candidate => candidate.Text)
            .First();
    }

    private static double ScoreCalculatorOcrCandidate(CalculatorOcrCandidate candidate)
    {
        double score = candidate.Confidence * 100;
        if (IsPlausibleCalculatorDisplayText(candidate.Text))
            score += 1000;

        score -= candidate.Text.Length * 2;

        if (candidate.Bounds is WindowBounds bounds)
        {
            score -= bounds.Y * 0.01;
            score += bounds.Width * 0.01;
        }

        return score;
    }

    private static double ScoreCalculatorDisplayCandidate(CalculatorOcrCandidate candidate)
    {
        double score = ScoreCalculatorOcrCandidate(candidate);
        if (IsResolvedCalculatorDisplayText(candidate.Text))
            score += 250;

        if (candidate.Bounds is WindowBounds bounds)
        {
            score += bounds.Y * 0.5;
            score += bounds.Height * 2;
        }

        return score;
    }

    private static bool IsPlausibleCalculatorDisplayText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
            text,
            @"^-?\d+(?:\.\d+)?(?:[+\-*/]-?\d*(?:\.\d+)?)?=?$",
            RegexOptions.CultureInvariant);
    }

    private static bool IsResolvedCalculatorDisplayText(string? text)
        => !string.IsNullOrWhiteSpace(text)
            && Regex.IsMatch(text, @"^-?\d+(?:\.\d+)?=?$", RegexOptions.CultureInvariant);

    private static bool TryGetCalculatorDisplayRegionFromUiSummary(string? uiSummary, out WindowBounds region)
    {
        region = default;
        if (string.IsNullOrWhiteSpace(uiSummary))
            return false;

        Match scrollAreaMatch = Regex.Match(
            uiSummary,
            @"AXScrollArea\s*\|\s*x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        if (scrollAreaMatch.Success)
        {
            region = new WindowBounds(
                int.Parse(scrollAreaMatch.Groups["x"].Value),
                int.Parse(scrollAreaMatch.Groups["y"].Value),
                int.Parse(scrollAreaMatch.Groups["w"].Value),
                int.Parse(scrollAreaMatch.Groups["h"].Value));
            return true;
        }

        Match staticTextMatch = Regex.Match(
            uiSummary,
            @"AXStaticText\s*\|.*?x=(?<x>\d+),y=(?<y>\d+),w=(?<w>\d+),h=(?<h>\d+)",
            RegexOptions.CultureInvariant);

        if (!staticTextMatch.Success)
            return false;

        int x = int.Parse(staticTextMatch.Groups["x"].Value);
        int y = int.Parse(staticTextMatch.Groups["y"].Value);
        int width = int.Parse(staticTextMatch.Groups["w"].Value);
        int height = int.Parse(staticTextMatch.Groups["h"].Value);
        region = new WindowBounds(Math.Max(0, x - 180), Math.Max(0, y - 6), width + 180, height + 12);
        return true;
    }

    private static IEnumerable<WindowBounds> GetCalculatorDisplayRegions(WindowBounds captureBounds)
    {
        yield return CreateRelativeRegion(captureBounds, 0.17, 0.13, 0.66, 0.16);
        yield return CreateRelativeRegion(captureBounds, 0.10, 0.08, 0.80, 0.22);
        yield return CreateRelativeRegion(captureBounds, 0.06, 0.04, 0.88, 0.28);
    }

    private static WindowBounds CreateRelativeRegion(WindowBounds captureBounds, double relativeX, double relativeY, double relativeWidth, double relativeHeight)
    {
        int x = captureBounds.X + (int)Math.Round(captureBounds.Width * relativeX);
        int y = captureBounds.Y + (int)Math.Round(captureBounds.Height * relativeY);
        int width = (int)Math.Round(captureBounds.Width * relativeWidth);
        int height = (int)Math.Round(captureBounds.Height * relativeHeight);
        return new WindowBounds(x, y, width, height);
    }

    private static byte[] CropScreenshotToWindowBoundsRegion(byte[] screenshotBytes, WindowBounds captureBounds, WindowBounds region)
    {
        using SKBitmap source = SKBitmap.Decode(screenshotBytes)
            ?? throw new InvalidOperationException("Failed to decode calculator screenshot for OCR cropping.");

        SKRectI cropRect = new(
            Math.Max(0, region.X - captureBounds.X),
            Math.Max(0, region.Y - captureBounds.Y),
            Math.Min(source.Width, region.X - captureBounds.X + region.Width),
            Math.Min(source.Height, region.Y - captureBounds.Y + region.Height));

        if (cropRect.Width <= 0 || cropRect.Height <= 0)
            throw new InvalidOperationException("Calculator display crop is outside the captured image.");

        using SKBitmap cropped = new(cropRect.Width, cropRect.Height, source.ColorType, source.AlphaType);
        if (!source.ExtractSubset(cropped, cropRect))
            throw new InvalidOperationException("Failed to crop calculator display region.");

        using SKImage image = SKImage.FromBitmap(cropped);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] PreprocessCalculatorDisplayImage(byte[] screenshotBytes)
    {
        using SKBitmap source = SKBitmap.Decode(screenshotBytes)
            ?? throw new InvalidOperationException("Failed to decode calculator screenshot for OCR preprocessing.");

        int scaledWidth = Math.Max(source.Width * 3, source.Width + 1);
        int scaledHeight = Math.Max(source.Height * 3, source.Height + 1);
        using SKBitmap scaled = source.Resize(new SKImageInfo(scaledWidth, scaledHeight, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High)
            ?? throw new InvalidOperationException("Failed to scale calculator screenshot for OCR preprocessing.");

        using SKBitmap processed = new(scaledWidth, scaledHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < scaledHeight; y++)
        {
            for (int x = 0; x < scaledWidth; x++)
            {
                SKColor pixel = scaled.GetPixel(x, y);
                int luminance = (int)Math.Round((pixel.Red * 0.2126) + (pixel.Green * 0.7152) + (pixel.Blue * 0.0722));
                byte value = luminance >= 150 ? (byte)255 : (byte)0;
                processed.SetPixel(x, y, new SKColor(value, value, value, 255));
            }
        }

        using SKImage image = SKImage.FromBitmap(processed);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] BuildPreparedCalculatorTargetImage(byte[] sourceScreenshotBytes, WindowBounds captureBounds, RuntimeCalculatorButtonTarget target)
        => ScreenshotAnnotator.Annotate(
            sourceScreenshotBytes,
            new ScreenshotAnnotationData(
                captureBounds,
                target.CenterX,
                target.CenterY,
                ScreenshotVisualStyles.SchematicTarget,
                IntendedClickTarget: new ScreenshotClickTarget(target.CenterX, target.CenterY, target.Label),
                IntendedElementRegion: new ScreenshotHighlightedRegion(target.Bounds, target.Label)));

    private static async Task<LlmPreparedStepAssessment> AssessPreparedStepWithLlmAsync(
        ChatClient client,
        string uiSummary,
        ScreenshotModelAttachment attachment,
        byte[] preparedImageBytes,
        string preparedImageMediaType,
        LiveCalculatorStep step)
    {
        UserChatMessage userMessage = new(
            ChatMessageContentPart.CreateTextPart($"Prüfe einen Rechner-Automationsschritt. Erwarte als nächstes die Taste calculator_key={step.TargetKey}. Beurteile das Ziel primär aus Originalbild, aufbereitetem Zielscreen und AX-Zusammenfassung. Ignoriere OCR-Rauschen vor dem Klick, solange das sichtbare Display und die Zielmarkierung konsistent sind. AX-Zusammenfassung:\n{uiSummary}\nAntworte nur als JSON mit den Feldern targetLabel, shouldClick und reason. shouldClick darf nur true sein, wenn Originalbild, aufbereiteter Zielscreen und AX-Zusammenfassung konsistent dieselbe Taste bestätigen."),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(attachment.Bytes), attachment.MediaType, ChatImageDetailLevel.High),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(preparedImageBytes), preparedImageMediaType, ChatImageDetailLevel.High));

        IReadOnlyList<ChatMessage> messages =
        [
            new SystemChatMessage("Du validierst Rechner-Klickziele. Antworte ausschließlich mit kompaktem JSON und erfinde nichts."),
            userMessage,
        ];

        ClientResult<ChatCompletion> completion = await client.CompleteChatAsync(messages);

        return DeserializeJsonResponse<LlmPreparedStepAssessment>(completion.Value);
    }

    private static async Task<LlmAfterStepAssessment> AssessAfterStepWithLlmAsync(
        ChatClient client,
        string uiSummary,
        ScreenshotModelAttachment attachment,
        LiveCalculatorStep step,
        string afterOcrText)
    {
        UserChatMessage userMessage = new(
            ChatMessageContentPart.CreateTextPart($"Prüfe den Zustand des Rechners nach einem Klick. Erwarteter sichtbarer Zustand: {step.ExpectedDisplayAfterClick}. OCR nach dem Klick: {afterOcrText}. AX-Zusammenfassung:\n{uiSummary}\nAntworte nur als JSON mit den Feldern stateConfirmed, visibleState und reason. stateConfirmed darf nur true sein, wenn der neue Zustand sichtbar bestätigt ist."),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(attachment.Bytes), attachment.MediaType, ChatImageDetailLevel.High));

        IReadOnlyList<ChatMessage> messages =
        [
            new SystemChatMessage("Du validierst sichtbare Rechnerzustände. Antworte ausschließlich mit kompaktem JSON und erfinde nichts."),
            userMessage,
        ];

        ClientResult<ChatCompletion> completion = await client.CompleteChatAsync(messages);

        return DeserializeJsonResponse<LlmAfterStepAssessment>(completion.Value);
    }

    private static T DeserializeJsonResponse<T>(ChatCompletion completion)
    {
        string content = string.Concat(completion.Content.Select(static part => part.Text)).Trim();
        T? value = JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(value);
        return value!;
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

internal sealed class MutableRuntimeCapturedScreenshotService : IScreenshotService
{
    private byte[] _screenshotBytes;
    private readonly ScreenInfo _screenInfo;

    public MutableRuntimeCapturedScreenshotService(byte[] screenshotBytes, ScreenInfo screenInfo)
    {
        _screenshotBytes = screenshotBytes;
        _screenInfo = screenInfo;
    }

    public ScreenshotCaptureOptions LastOptions;

    public void SetScreenshotBytes(byte[] screenshotBytes)
        => _screenshotBytes = screenshotBytes;

    public byte[] TakeScreenshot(ScreenshotCaptureOptions options = default)
    {
        LastOptions = options;
        return _screenshotBytes;
    }

    public ScreenInfo GetScreenInfo() => _screenInfo;
}