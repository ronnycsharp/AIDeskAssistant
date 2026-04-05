using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using SkiaSharp;

namespace AIDeskAssistant.Tools;

/// <summary>Executes desktop tool calls dispatched by the OpenAI model.</summary>
internal sealed class DesktopToolExecutor
{
    internal const string ErrorPrefix = "[ERROR] ";
    private const string ApplicationNameArg = "application_name";
    private const string PurposeArg = "purpose";
    private const string RegionHeightArg = "region_height";
    private const string RegionWidthArg = "region_width";
    private const string RegionXArg = "region_x";
    private const string RegionYArg = "region_y";
    private const string TimeoutMsArg = "timeout_ms";
    private const string TitleArg = "title";
    private const string ValueArg = "value";
    private const string WidthArg = "width";
    private const string HeightArg = "height";
    private const string VisualStyleArg = "visual_style";
    private const string FullScreenScreenshotTarget = "full_screen";
    private const string ActiveWindowScreenshotTarget = "active_window";
    private static readonly string[] MacOSAccessibilityPermissionErrorMarkers =
    [
        "-25211",
        "Hilfszugriff",
        "assistive access",
        "accessibility",
        "system events got an error",
    ];
    private static readonly HashSet<string> SpecialInputTextTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "up",
        "down",
        "left",
        "right",
        "enter",
        "return",
        "tab",
        "escape",
        "esc",
        "backspace",
        "delete",
        "del",
        "home",
        "end",
        "pageup",
        "pagedown",
        "pgup",
        "pgdown",
        "page-up",
        "page-down",
    };

    private static readonly HashSet<string> SpecialInputTwoWordPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "page up",
        "page down",
    };

    private readonly IScreenshotService _screenshot;
    private readonly ScreenshotOptimizer _screenshotOptimizer;
    private readonly IMouseService _mouse;
    private readonly IKeyboardService _keyboard;
    private readonly ITerminalService _terminal;
    private readonly IWindowService _window;
    private readonly IUiAutomationService _uiAutomation;
    private readonly ITextRecognitionService _textRecognition;
    private readonly object _markSync = new();
    private Dictionary<int, ScreenshotMark> _latestScreenshotMarks = [];

    public DesktopToolExecutor(
        IScreenshotService screenshot,
        IMouseService mouse,
        IKeyboardService keyboard,
        ITerminalService terminal,
        IWindowService window,
        IUiAutomationService uiAutomation,
        ITextRecognitionService textRecognition)
    {
        _screenshot = screenshot;
        _screenshotOptimizer = new ScreenshotOptimizer(ScreenshotOptimizer.ReadFromEnvironment());
        _mouse = mouse;
        _keyboard = keyboard;
        _terminal = terminal;
        _window = window;
        _uiAutomation = uiAutomation;
        _textRecognition = textRecognition;
    }

    public string Execute(string toolName, string argsJson)
    {
        try
        {
            var args = DesktopToolDefinitions.ParseArgs(argsJson);
            MenuBarActivityState.ToolStarted(toolName, SummarizeArguments(argsJson));

            string result = toolName switch
            {
                "take_screenshot" => TakeScreenshot(args),
                "get_screen_info" => GetScreenInfo(),
                "read_screen_text" => ReadScreenText(args),
                "get_frontmost_ui_elements" => GetFrontmostUiElements(),
                "get_frontmost_application" => GetFrontmostApplication(),
                "list_windows" => ListWindows(),
                "get_cursor_position" => GetCursorPosition(),
                "move_mouse" => MoveMouse(args),
                "drag" => Drag(args),
                "click" => Click(args),
                "double_click" => DoubleClick(args),
                "scroll" => Scroll(args),
                "type_text" => TypeText(args),
                "word_create_document" => WordCreateDocument(args),
                "word_set_document_text" => WordSetDocumentText(args),
                "word_replace_text" => WordReplaceText(args),
                "word_format_text" => WordFormatText(args),
                "press_key" => PressKey(args),
                "open_application" => OpenApplication(args),
                "focus_application" => FocusApplication(args),
                "open_url" => OpenUrl(args),
                "run_command" => RunCommand(args),
                "click_dock_application" => ClickDockApplication(args),
                "click_apple_menu_item" => ClickAppleMenuItem(args),
                "click_system_settings_sidebar_item" => ClickSystemSettingsSidebarItem(args),
                "focus_window" => FocusWindow(args),
                "wait_for_window" => WaitForWindow(args),
                "focus_frontmost_window_content" => FocusFrontmostWindowContent(args),
                "find_ui_element" => FindUiElement(args),
                "click_ui_element" => ClickUiElement(args),
                "wait_for_ui_element" => WaitForUiElement(args),
                "get_focused_ui_element" => GetFocusedUiElement(),
                "assert_state" => AssertState(args),
                "get_active_window_bounds" => GetActiveWindowBounds(),
                "move_active_window" => MoveActiveWindow(args),
                "resize_active_window" => ResizeActiveWindow(args),
                "wait" => Wait(args),
                _ => Err($"Unknown tool: {toolName}"),
            };

            if (IsErrorResult(result))
                MenuBarActivityState.ToolFailed(toolName, SummarizeToolResult(result));
            else
                MenuBarActivityState.ToolFinished(toolName, SummarizeToolResult(result));

            return result;
        }
        catch (Exception ex)
        {
            MenuBarActivityState.ToolFailed(toolName, ex.Message);
            return Err($"Tool '{toolName}' failed: {ex.Message}");
        }
    }

    internal static bool IsErrorResult(string result)
        => result.StartsWith(ErrorPrefix, StringComparison.Ordinal);

    internal static string Err(string message)
        => IsErrorResult(message) ? message : $"{ErrorPrefix}{message}";

    private static string? SummarizeArguments(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "{}")
            return null;

        string normalized = argsJson.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }

    private static string SummarizeToolResult(string result)
    {
        int base64Index = result.IndexOf("Base64:", StringComparison.Ordinal);
        string summary = base64Index >= 0 ? result[..base64Index].TrimEnd() : result;
        summary = summary.ReplaceLineEndings(" ").Trim();
        return summary.Length <= 140 ? summary : summary[..140] + "...";
    }

    private string TakeScreenshot(Dictionary<string, JsonElement> args)
    {
        string target = DesktopToolDefinitions.GetString(args, "target", FullScreenScreenshotTarget)
            .Trim()
            .ToLowerInvariant();
        string purpose = DesktopToolDefinitions.GetString(args, PurposeArg).Trim();
        string visualStyle = NormalizeScreenshotVisualStyle(DesktopToolDefinitions.GetString(args, VisualStyleArg, ScreenshotVisualStyles.Standard));
        int padding = Math.Clamp(DesktopToolDefinitions.GetInt(args, "padding", 16), 0, 200);

        if (!TryResolveScreenshotCaptureOptions(target, padding, out ScreenshotCaptureOptions options, out WindowBounds? bounds, out string error))
            return error;

        ScreenInfo screenInfo = _screenshot.GetScreenInfo();
        WindowBounds captureBounds = bounds ?? new WindowBounds(0, 0, screenInfo.Width, screenInfo.Height);
        WindowBounds? suggestedContentArea = string.Equals(target, ActiveWindowScreenshotTarget, StringComparison.Ordinal)
            ? ScreenshotAnnotationData.CreateSuggestedContentArea(captureBounds)
            : null;
        var (cursorX, cursorY) = _mouse.GetPosition();
        ScreenshotPrediction? prediction = TryGetScreenshotPrediction(args);
        ScreenshotHighlightedRegion? intendedElementRegion = TryGetIntendedElementRegion(args);
        ScreenshotClickTarget? intendedClickTarget = ResolveEffectiveIntendedClickTarget(TryGetIntendedClickTarget(args), intendedElementRegion);

        if (string.Equals(visualStyle, ScreenshotVisualStyles.SchematicTarget, StringComparison.Ordinal)
            && intendedClickTarget is null
            && intendedElementRegion is null)
        {
            return Err("visual_style 'schematic_target' requires intended_click coordinates or an intended_element region.");
        }

        WindowHitTestResult? windowUnderCursor = TryGetWindowUnderCursor(cursorX, cursorY);

        byte[] screenshot = _screenshot.TakeScreenshot(options);
        (bool marksRequested, IReadOnlyList<ScreenshotMark> marks) = BuildScreenshotMarks(args, captureBounds, screenshot);
        SetLatestScreenshotMarks(marks);
        var annotation = new ScreenshotAnnotationData(captureBounds, cursorX, cursorY, visualStyle, suggestedContentArea, intendedClickTarget, intendedElementRegion, marks);
        ScreenshotToolImages images = BuildScreenshotToolImages(screenshot, annotation);
        var context = new ScreenshotResultContext(target, purpose, visualStyle, prediction, captureBounds, cursorX, cursorY, suggestedContentArea, intendedClickTarget, intendedElementRegion, windowUnderCursor, marksRequested, marks);
        return BuildScreenshotToolResult(images, context);
    }

    private static string NormalizeScreenshotVisualStyle(string? visualStyle)
    {
        string normalized = visualStyle?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            ScreenshotVisualStyles.SchematicTarget => ScreenshotVisualStyles.SchematicTarget,
            _ => ScreenshotVisualStyles.Standard,
        };
    }

    private string ReadScreenText(Dictionary<string, JsonElement> args)
    {
        string target = DesktopToolDefinitions.GetString(args, "target", ActiveWindowScreenshotTarget)
            .Trim()
            .ToLowerInvariant();
        string purpose = DesktopToolDefinitions.GetString(args, PurposeArg).Trim();
        int padding = Math.Clamp(DesktopToolDefinitions.GetInt(args, "padding", 16), 0, 200);

        if (!TryResolveScreenshotCaptureOptions(target, padding, out ScreenshotCaptureOptions options, out WindowBounds? captureBounds, out string error))
            return error;

        try
        {
            ScreenInfo screenInfo = _screenshot.GetScreenInfo();
            WindowBounds resolvedCaptureBounds = captureBounds ?? new WindowBounds(0, 0, screenInfo.Width, screenInfo.Height);
            byte[] screenshot = _screenshot.TakeScreenshot(options);
            WindowBounds? requestedRegion = TryGetRequestedRegion(args);
            if (requestedRegion is null && TryGetOptionalInt(args, "mark_id", out int requestedMarkId))
            {
                if (!TryGetLatestScreenshotMark(requestedMarkId, out ScreenshotMark requestedMark, out string markError))
                    return markError;

                requestedRegion = requestedMark.Bounds;
            }

            WindowBounds effectiveRegion = ResolveEffectiveOcrRegion(requestedRegion, resolvedCaptureBounds);
            byte[] croppedImageBytes = CropScreenshotToRegion(screenshot, resolvedCaptureBounds, effectiveRegion);
            TextRecognitionResult ocrResult = _textRecognition.RecognizeText(croppedImageBytes);

            return BuildScreenTextToolResult(target, purpose, resolvedCaptureBounds, effectiveRegion, ocrResult);
        }
        catch (Exception ex)
        {
            return Err($"Failed to read screen text: {ex.Message}");
        }
    }

    private static ScreenshotClickTarget? TryGetIntendedClickTarget(Dictionary<string, JsonElement> args)
    {
        if (!TryGetOptionalInt(args, "intended_click_x", out int x)
            || !TryGetOptionalInt(args, "intended_click_y", out int y))
        {
            return null;
        }

        string label = DesktopToolDefinitions.GetString(args, "intended_click_label").Trim();
        return new ScreenshotClickTarget(x, y, string.IsNullOrWhiteSpace(label) ? null : label);
    }

    private static ScreenshotClickTarget? ResolveEffectiveIntendedClickTarget(ScreenshotClickTarget? requestedClickTarget, ScreenshotHighlightedRegion? intendedElementRegion)
    {
        if (intendedElementRegion is not ScreenshotHighlightedRegion region)
            return requestedClickTarget;

        int centerX = region.Bounds.X + (region.Bounds.Width / 2);
        int centerY = region.Bounds.Y + (region.Bounds.Height / 2);
        string? label = string.IsNullOrWhiteSpace(region.Label) ? requestedClickTarget?.Label : region.Label;
        return new ScreenshotClickTarget(centerX, centerY, label);
    }

    private static ScreenshotPrediction? TryGetScreenshotPrediction(Dictionary<string, JsonElement> args)
    {
        string predictedTool = DesktopToolDefinitions.GetString(args, "predicted_tool").Trim();
        string predictedAction = DesktopToolDefinitions.GetString(args, "predicted_action").Trim();
        string predictedTargetLabel = DesktopToolDefinitions.GetString(args, "predicted_target_label").Trim();

        if (string.IsNullOrWhiteSpace(predictedTool)
            && string.IsNullOrWhiteSpace(predictedAction)
            && string.IsNullOrWhiteSpace(predictedTargetLabel))
        {
            return null;
        }

        return new ScreenshotPrediction(
            string.IsNullOrWhiteSpace(predictedTool) ? null : predictedTool,
            string.IsNullOrWhiteSpace(predictedAction) ? null : predictedAction,
            string.IsNullOrWhiteSpace(predictedTargetLabel) ? null : predictedTargetLabel);
    }

    private static ScreenshotHighlightedRegion? TryGetIntendedElementRegion(Dictionary<string, JsonElement> args)
    {
        if (!TryGetOptionalInt(args, "intended_element_x", out int x)
            || !TryGetOptionalInt(args, "intended_element_y", out int y)
            || !TryGetOptionalInt(args, "intended_element_width", out int width)
            || !TryGetOptionalInt(args, "intended_element_height", out int height))
        {
            return null;
        }

        if (width <= 0 || height <= 0)
            return null;

        string label = DesktopToolDefinitions.GetString(args, "intended_element_label").Trim();
        return new ScreenshotHighlightedRegion(new WindowBounds(x, y, width, height), string.IsNullOrWhiteSpace(label) ? null : label);
    }

    private static WindowBounds? TryGetRequestedRegion(Dictionary<string, JsonElement> args)
    {
        if (!TryGetOptionalInt(args, RegionXArg, out int x)
            || !TryGetOptionalInt(args, RegionYArg, out int y)
            || !TryGetOptionalInt(args, RegionWidthArg, out int width)
            || !TryGetOptionalInt(args, RegionHeightArg, out int height))
        {
            return null;
        }

        if (width <= 0 || height <= 0)
            return null;

        return new WindowBounds(x, y, width, height);
    }

    private WindowHitTestResult? TryGetWindowUnderCursor(int cursorX, int cursorY)
    {
        try
        {
            return _window.GetWindowAtPoint(cursorX, cursorY);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetOptionalInt(Dictionary<string, JsonElement> args, string key, out int value)
    {
        if (args.TryGetValue(key, out JsonElement element) && element.ValueKind == JsonValueKind.Number)
        {
            value = element.GetInt32();
            return true;
        }

        value = default;
        return false;
    }

    private bool TryResolveScreenshotCaptureOptions(
        string target,
        int padding,
        out ScreenshotCaptureOptions options,
        out WindowBounds? bounds,
        out string error)
    {
        options = default;
        bounds = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, FullScreenScreenshotTarget, StringComparison.Ordinal))
            return true;

        if (!string.Equals(target, ActiveWindowScreenshotTarget, StringComparison.Ordinal))
        {
            error = Err($"Invalid screenshot target: '{target}'. Supported values: '{FullScreenScreenshotTarget}', '{ActiveWindowScreenshotTarget}'.");
            return false;
        }

        try
        {
            WindowBounds activeWindow = _window.GetActiveWindowBounds();
            WindowBounds clampedBounds = ClampWindowBounds(activeWindow, padding, _screenshot.GetScreenInfo());
            if (clampedBounds.Width <= 0 || clampedBounds.Height <= 0)
            {
                error = Err("Failed to capture active window screenshot: active window bounds are empty.");
                return false;
            }

            bounds = clampedBounds;
            options = new ScreenshotCaptureOptions(clampedBounds);
            return true;
        }
        catch (Exception ex)
        {
            error = Err($"Failed to capture active window screenshot: {ex.Message}");
            return false;
        }
    }

    private static WindowBounds ClampWindowBounds(WindowBounds bounds, int padding, ScreenInfo screenInfo)
    {
        int x = Math.Max(0, bounds.X - padding);
        int y = Math.Max(0, bounds.Y - padding);
        int maxWidth = Math.Max(0, screenInfo.Width - x);
        int maxHeight = Math.Max(0, screenInfo.Height - y);
        int width = Math.Min(maxWidth, Math.Max(0, bounds.Width + (padding * 2)));
        int height = Math.Min(maxHeight, Math.Max(0, bounds.Height + (padding * 2)));
        return new WindowBounds(x, y, width, height);
    }

    private static WindowBounds ResolveEffectiveOcrRegion(WindowBounds? requestedRegion, WindowBounds captureBounds)
    {
        if (requestedRegion is null)
            return captureBounds;

        int x = Math.Max(captureBounds.X, requestedRegion.Value.X);
        int y = Math.Max(captureBounds.Y, requestedRegion.Value.Y);
        int maxX = Math.Min(captureBounds.X + captureBounds.Width, requestedRegion.Value.X + requestedRegion.Value.Width);
        int maxY = Math.Min(captureBounds.Y + captureBounds.Height, requestedRegion.Value.Y + requestedRegion.Value.Height);
        int width = Math.Max(0, maxX - x);
        int height = Math.Max(0, maxY - y);
        return width <= 0 || height <= 0 ? captureBounds : new WindowBounds(x, y, width, height);
    }

    private static byte[] CropScreenshotToRegion(byte[] screenshotBytes, WindowBounds captureBounds, WindowBounds region)
    {
        if (region == captureBounds)
            return screenshotBytes;

        using SKBitmap source = SKBitmap.Decode(screenshotBytes)
            ?? throw new InvalidOperationException("Failed to decode screenshot for OCR.");

        SKRectI cropRect = new(
            Math.Max(0, region.X - captureBounds.X),
            Math.Max(0, region.Y - captureBounds.Y),
            Math.Min(source.Width, region.X - captureBounds.X + region.Width),
            Math.Min(source.Height, region.Y - captureBounds.Y + region.Height));

        if (cropRect.Width <= 0 || cropRect.Height <= 0)
            throw new InvalidOperationException("The requested OCR region is outside the captured image.");

        using SKBitmap cropped = new(cropRect.Width, cropRect.Height, source.ColorType, source.AlphaType);
        if (!source.ExtractSubset(cropped, cropRect))
            throw new InvalidOperationException("Failed to crop screenshot for OCR.");

        using SKImage image = SKImage.FromBitmap(cropped);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static string BuildScreenTextToolResult(
        string target,
        string purpose,
        WindowBounds captureBounds,
        WindowBounds ocrRegion,
        TextRecognitionResult result)
    {
        var parts = new List<string>
        {
            $"OCR read completed. Target: {target}.",
            $"Capture bounds: X={captureBounds.X}, Y={captureBounds.Y}, Width={captureBounds.Width}, Height={captureBounds.Height}.",
            $"OCR region: X={ocrRegion.X}, Y={ocrRegion.Y}, Width={ocrRegion.Width}, Height={ocrRegion.Height}.",
        };

        if (!string.IsNullOrWhiteSpace(purpose))
            parts.Add($"Purpose: {purpose}.");

        if (result.Lines.Count == 0)
        {
            parts.Add("Recognized text: (none).");
            return string.Join(" ", parts);
        }

        parts.Add($"Recognized text:\n{result.FullText}");
        parts.Add("Recognized lines:");
        parts.AddRange(result.Lines.Select((line, index) =>
            $"[{index}] Text={line.Text}, Confidence={line.Confidence.ToString("F2", CultureInfo.InvariantCulture)}, X={line.Bounds.X}, Y={line.Bounds.Y}, Width={line.Bounds.Width}, Height={line.Bounds.Height}"));
        return string.Join(Environment.NewLine, parts);
    }

    private ScreenshotToolImages BuildScreenshotToolImages(byte[] sourceScreenshot, ScreenshotAnnotationData annotation)
    {
        bool processedPrimary = string.Equals(annotation.VisualStyle, ScreenshotVisualStyles.SchematicTarget, StringComparison.Ordinal);
        byte[] primaryBytes = processedPrimary
            ? ScreenshotAnnotator.Annotate(sourceScreenshot, annotation)
            : sourceScreenshot;

        ScreenshotPayload primaryPayload = _screenshotOptimizer.Optimize(primaryBytes);
        var supplementalImages = new List<ScreenshotToolImage>();

        if (!processedPrimary && ShouldCreateDerivedTargetView(annotation))
        {
            byte[] schematicBytes = ScreenshotAnnotator.Annotate(sourceScreenshot, annotation with { VisualStyle = ScreenshotVisualStyles.SchematicTarget });
            ScreenshotPayload schematicPayload = _screenshotOptimizer.Optimize(schematicBytes);
            supplementalImages.Add(new ScreenshotToolImage("schematic-target", schematicPayload));
        }

        return new ScreenshotToolImages(primaryPayload, supplementalImages);
    }

    private static bool ShouldCreateDerivedTargetView(ScreenshotAnnotationData annotation)
        => annotation.HasIntendedClickTarget || annotation.HasIntendedElementRegion;

    private static string BuildScreenshotToolResult(ScreenshotToolImages images, ScreenshotResultContext context)
    {
        var annotation = new ScreenshotAnnotationData(context.CaptureBounds, context.CursorX, context.CursorY, context.VisualStyle, context.SuggestedContentArea, context.IntendedClickTarget, context.IntendedElementRegion, context.Marks);
        var parts = new List<string>
        {
            "Screenshot taken.",
            $"Target: {context.Target}.",
        };

        parts.Add($"Visual style: {context.VisualStyle}.");

        if (!string.IsNullOrWhiteSpace(context.Purpose))
            parts.Add($"Purpose: {context.Purpose}.");

        if (string.Equals(context.VisualStyle, ScreenshotVisualStyles.SchematicTarget, StringComparison.Ordinal))
            parts.Add("Schematic target overview: the background is desaturated and dimmed so the intended target/button stands out more strongly for pre-click validation.");
        else if (images.SupplementalImages.Count > 0)
            parts.Add("Derived schematic target view included: this second image was generated from the same original screenshot and highlights the intended control for the next click or keyboard action.");

        AddScreenshotPredictionParts(parts, context.Prediction);

        parts.Add($"Capture bounds: X={context.CaptureBounds.X}, Y={context.CaptureBounds.Y}, Width={context.CaptureBounds.Width}, Height={context.CaptureBounds.Height}.");
        if (context.WindowUnderCursor is WindowHitTestResult window)
            parts.Add($"Window under cursor: App={window.ApplicationName}, Title={FormatWindowTitle(window.Title)}, X={window.Bounds.X}, Y={window.Bounds.Y}, Width={window.Bounds.Width}, Height={window.Bounds.Height}.");
        if (annotation.HasSuggestedContentArea && annotation.SuggestedContentArea is WindowBounds contentArea)
            parts.Add($"Likely content area: X={contentArea.X}, Y={contentArea.Y}, Width={contentArea.Width}, Height={contentArea.Height}. Prefer clicks and typing inside this region unless the screenshot shows a more specific control elsewhere.");
        AddScreenshotTargetingParts(parts, annotation, context);
        if (context.Marks.Count > 0)
            parts.Add($"Numbered marks: {string.Join(" ", context.Marks.Select(mark => $"[{mark.Id}] Source={mark.Source}, Label={FormatWindowTitle(mark.Label)}, X={mark.Bounds.X}, Y={mark.Bounds.Y}, Width={mark.Bounds.Width}, Height={mark.Bounds.Height}."))} Prefer follow-up actions with mark_id when a matching mark already exists.");
        else if (context.MarksRequested)
            parts.Add("Numbered marks: none found for the requested mark filters/source.");

        parts.Add($"Original: {images.PrimaryImage.Payload.OriginalByteCount} bytes.");
        parts.Add($"Final: {images.PrimaryImage.Payload.FinalByteCount} bytes.");
        parts.Add($"Saved: {images.PrimaryImage.Payload.BytesSaved} bytes ({images.PrimaryImage.Payload.SavingsRatio:P1}).");
        parts.Add($"Resolution: {images.PrimaryImage.Payload.Width}x{images.PrimaryImage.Payload.Height}.");
        parts.Add($"Media type: {images.PrimaryImage.Payload.MediaType}.");
        parts.Add($"Base64: {Convert.ToBase64String(images.PrimaryImage.Payload.Bytes)}");

        foreach (ScreenshotToolImage supplementalImage in images.SupplementalImages)
        {
            parts.Add($"Supplemental image [{supplementalImage.Label}] media type: {supplementalImage.Payload.MediaType}.");
            parts.Add($"Supplemental image [{supplementalImage.Label}] base64: {Convert.ToBase64String(supplementalImage.Payload.Bytes)}");
        }

        return string.Join(" ", parts);
    }

    private static void AddScreenshotPredictionParts(List<string> parts, ScreenshotPrediction? prediction)
    {
        if (prediction is not ScreenshotPrediction value)
            return;

        if (!string.IsNullOrWhiteSpace(value.ToolName))
            parts.Add($"Predicted next tool: {value.ToolName}.");
        if (!string.IsNullOrWhiteSpace(value.Action))
            parts.Add($"Predicted next action: {value.Action}.");
        if (!string.IsNullOrWhiteSpace(value.TargetLabel))
            parts.Add($"Predicted target/button: {FormatWindowTitle(value.TargetLabel)}.");
    }

    private static void AddScreenshotTargetingParts(List<string> parts, ScreenshotAnnotationData annotation, ScreenshotResultContext context)
    {
        if (context.IntendedElementRegion is ScreenshotHighlightedRegion highlightedRegion)
        {
            parts.Add($"Highlighted AX element: X={highlightedRegion.Bounds.X}, Y={highlightedRegion.Bounds.Y}, Width={highlightedRegion.Bounds.Width}, Height={highlightedRegion.Bounds.Height}, IntersectsCapture={annotation.IntendedElementRegionIntersectsCapture}, Label={FormatWindowTitle(highlightedRegion.Label)}.");
            if (context.IntendedClickTarget is ScreenshotClickTarget axCenterClickTarget)
                parts.Add($"AX-derived click target: X={axCenterClickTarget.X}, Y={axCenterClickTarget.Y}, InsideCapture={annotation.IntendedClickIsInsideCapture}, Label={FormatWindowTitle(axCenterClickTarget.Label)}. Use the center point of the AX element as the click position.");

            return;
        }

        if (context.IntendedClickTarget is ScreenshotClickTarget clickTarget)
            parts.Add($"Intended click target: X={clickTarget.X}, Y={clickTarget.Y}, InsideCapture={annotation.IntendedClickIsInsideCapture}, Label={FormatWindowTitle(clickTarget.Label)}. Use the highlighted target marker to validate the click before you press click or double_click.");
    }

    private static string FormatWindowTitle(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<untitled>" : value;

    private readonly record struct ScreenshotResultContext(
        string Target,
        string Purpose,
        string VisualStyle,
        ScreenshotPrediction? Prediction,
        WindowBounds CaptureBounds,
        int CursorX,
        int CursorY,
        WindowBounds? SuggestedContentArea,
        ScreenshotClickTarget? IntendedClickTarget,
        ScreenshotHighlightedRegion? IntendedElementRegion,
        WindowHitTestResult? WindowUnderCursor,
        bool MarksRequested,
        IReadOnlyList<ScreenshotMark> Marks);

    private readonly record struct ScreenshotToolImages(ScreenshotToolImage PrimaryImage, IReadOnlyList<ScreenshotToolImage> SupplementalImages)
    {
        public ScreenshotToolImages(ScreenshotPayload primaryPayload, IReadOnlyList<ScreenshotToolImage> supplementalImages)
            : this(new ScreenshotToolImage("main", primaryPayload), supplementalImages)
        {
        }
    }

    private readonly record struct ScreenshotToolImage(string Label, ScreenshotPayload Payload);

    private readonly record struct ScreenshotPrediction(string? ToolName, string? Action, string? TargetLabel);

    private (bool Requested, IReadOnlyList<ScreenshotMark> Marks) BuildScreenshotMarks(Dictionary<string, JsonElement> args, WindowBounds captureBounds, byte[] screenshotBytes)
    {
        string markSource = DesktopToolDefinitions.GetString(args, "mark_source", "none").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(markSource) || string.Equals(markSource, "none", StringComparison.Ordinal))
            return (false, Array.Empty<ScreenshotMark>());

        int maxCount = Math.Clamp(DesktopToolDefinitions.GetInt(args, "mark_max_count", 12), 1, 40);
        string titleFilter = DesktopToolDefinitions.GetString(args, "mark_title").Trim();
        string roleFilter = DesktopToolDefinitions.GetString(args, "mark_role").Trim();
        string valueFilter = DesktopToolDefinitions.GetString(args, "mark_value").Trim();
        string textFilter = DesktopToolDefinitions.GetString(args, "mark_text_contains").Trim();

        var candidates = new List<(WindowBounds Bounds, string Source, string Label)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        bool includeAx = string.Equals(markSource, "ax", StringComparison.Ordinal)
            || string.Equals(markSource, "ax_and_ocr", StringComparison.Ordinal);
        bool includeOcr = string.Equals(markSource, "ocr", StringComparison.Ordinal)
            || string.Equals(markSource, "ax_and_ocr", StringComparison.Ordinal);

        if (includeAx)
        {
            try
            {
                IReadOnlyList<UiElementInfo> elements = _uiAutomation.FindFrontmostUiElements(
                    string.IsNullOrWhiteSpace(titleFilter) ? null : titleFilter,
                    string.IsNullOrWhiteSpace(roleFilter) ? null : roleFilter,
                    string.IsNullOrWhiteSpace(valueFilter) ? null : valueFilter);

                foreach (UiElementInfo element in elements)
                {
                    if (element.Bounds is not WindowBounds bounds || bounds.Width <= 0 || bounds.Height <= 0 || !Intersects(captureBounds, bounds))
                        continue;

                    string label = BuildAxMarkLabel(element);
                    string key = $"ax|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}|{label}";
                    if (seen.Add(key))
                        candidates.Add((bounds, "ax", label));
                }
            }
            catch
            {
            }
        }

        if (includeOcr)
        {
            try
            {
                TextRecognitionResult ocr = _textRecognition.RecognizeText(screenshotBytes);
                foreach (TextRecognitionLine line in ocr.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Text))
                        continue;

                    if (!string.IsNullOrWhiteSpace(textFilter)
                        && line.Text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    WindowBounds bounds = new(captureBounds.X + line.Bounds.X, captureBounds.Y + line.Bounds.Y, line.Bounds.Width, line.Bounds.Height);
                    if (bounds.Width <= 0 || bounds.Height <= 0 || !Intersects(captureBounds, bounds))
                        continue;

                    string label = line.Text.Trim();
                    string key = $"ocr|{bounds.X}|{bounds.Y}|{bounds.Width}|{bounds.Height}|{label}";
                    if (seen.Add(key))
                        candidates.Add((bounds, "ocr", label));
                }
            }
            catch
            {
            }
        }

        IReadOnlyList<ScreenshotMark> marks = candidates
            .OrderBy(candidate => candidate.Bounds.Y)
            .ThenBy(candidate => candidate.Bounds.X)
            .Take(maxCount)
            .Select((candidate, index) => new ScreenshotMark(index + 1, candidate.Bounds, candidate.Source, candidate.Label))
            .ToArray();

        return (true, marks);
    }

    private static string BuildAxMarkLabel(UiElementInfo element)
    {
        if (!string.IsNullOrWhiteSpace(element.Title))
            return $"{element.Role} {element.Title}";

        if (!string.IsNullOrWhiteSpace(element.Value))
            return $"{element.Role} {element.Value}";

        return element.Role;
    }

    private void SetLatestScreenshotMarks(IReadOnlyList<ScreenshotMark> marks)
    {
        lock (_markSync)
        {
            _latestScreenshotMarks = marks.ToDictionary(mark => mark.Id);
        }
    }

    private bool TryGetLatestScreenshotMark(int markId, out ScreenshotMark mark, out string error)
    {
        lock (_markSync)
        {
            if (_latestScreenshotMarks.TryGetValue(markId, out mark))
            {
                error = string.Empty;
                return true;
            }
        }

        mark = default;
        error = Err($"Unknown mark_id {markId}. Call take_screenshot with mark_source first and then reuse one of the returned mark IDs.");
        return false;
    }

    private bool TryResolvePointFromArgs(Dictionary<string, JsonElement> args, out int x, out int y, out string descriptor, out string error)
    {
        if (TryGetOptionalInt(args, "mark_id", out int markId))
        {
            if (!TryGetLatestScreenshotMark(markId, out ScreenshotMark mark, out error))
            {
                x = 0;
                y = 0;
                descriptor = string.Empty;
                return false;
            }

            x = mark.Bounds.X + (mark.Bounds.Width / 2);
            y = mark.Bounds.Y + (mark.Bounds.Height / 2);
            descriptor = $"mark {mark.Id} ({mark.Source}: {mark.Label})";
            return true;
        }

        if (TryGetIntendedElementRegion(args) is ScreenshotHighlightedRegion intendedElement)
        {
            x = intendedElement.Bounds.X + (intendedElement.Bounds.Width / 2);
            y = intendedElement.Bounds.Y + (intendedElement.Bounds.Height / 2);
            descriptor = string.IsNullOrWhiteSpace(intendedElement.Label)
                ? "AX element center"
                : $"AX element center ({intendedElement.Label})";
            error = string.Empty;
            return true;
        }

        x = DesktopToolDefinitions.GetInt(args, "x");
        y = DesktopToolDefinitions.GetInt(args, "y");
        descriptor = "explicit coordinates";
        error = string.Empty;
        return true;
    }

    private static bool Intersects(WindowBounds captureBounds, WindowBounds bounds)
    {
        int left = Math.Max(captureBounds.X, bounds.X);
        int top = Math.Max(captureBounds.Y, bounds.Y);
        int right = Math.Min(captureBounds.X + captureBounds.Width, bounds.X + bounds.Width);
        int bottom = Math.Min(captureBounds.Y + captureBounds.Height, bounds.Y + bounds.Height);
        return right > left && bottom > top;
    }

    private string GetScreenInfo()
    {
        ScreenInfo info = _screenshot.GetScreenInfo();
        return $"Screen: {info.Width}x{info.Height}, {info.Depth} bpp";
    }

    private string GetFrontmostUiElements()
    {
        try
        {
            return _uiAutomation.SummarizeFrontmostUiElements();
        }
        catch (Exception ex)
        {
            return Err($"Frontmost UI element summary unavailable: {ex.Message}");
        }
    }

    private string GetFrontmostApplication()
    {
        try
        {
            return $"Frontmost application: {_window.GetFrontmostApplicationName()}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to get frontmost application: {ex.Message}");
        }
    }

    private string ListWindows()
    {
        try
        {
            IReadOnlyList<WindowInfo> windows = _window.ListWindows();
            if (windows.Count == 0)
                return "No windows found.";

            return string.Join(Environment.NewLine, windows.Select((window, index) =>
                $"[{index}] App={FormatWindowTitle(window.ApplicationName)}, Title={FormatWindowTitle(window.Title)}, X={window.Bounds.X}, Y={window.Bounds.Y}, Width={window.Bounds.Width}, Height={window.Bounds.Height}, Frontmost={window.IsFrontmost}, Minimized={window.IsMinimized}"));
        }
        catch (Exception ex) when (TryDescribeWindowEnumerationFallback(ex, out string? fallbackMessage))
        {
            return fallbackMessage;
        }
        catch (Exception ex)
        {
            return Err($"Failed to list windows: {ex.Message}");
        }
    }

    private string GetCursorPosition()
    {
        var (x, y) = _mouse.GetPosition();
        return $"Cursor position: ({x}, {y})";
    }

    private string MoveMouse(Dictionary<string, JsonElement> args)
    {
        if (!TryResolvePointFromArgs(args, out int x, out int y, out string descriptor, out string error))
            return error;

        _mouse.MoveTo(x, y);
        return $"Mouse moved to ({x}, {y}) via {descriptor}";
    }

    private string Drag(Dictionary<string, JsonElement> args)
    {
        int startX = DesktopToolDefinitions.GetInt(args, "start_x");
        int startY = DesktopToolDefinitions.GetInt(args, "start_y");
        int endX = DesktopToolDefinitions.GetInt(args, "end_x");
        int endY = DesktopToolDefinitions.GetInt(args, "end_y");
        string buttonName = DesktopToolDefinitions.GetString(args, "button", "left");
        MouseButton button = buttonName.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        _mouse.Drag(startX, startY, endX, endY, button);
        return $"Dragged {button} button from ({startX}, {startY}) to ({endX}, {endY})";
    }

    private string Click(Dictionary<string, JsonElement> args)
    {
        if (!TryResolvePointFromArgs(args, out int x, out int y, out string descriptor, out string error))
            return error;

        string buttonName = DesktopToolDefinitions.GetString(args, "button", "left");
        MouseButton button = buttonName.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        _mouse.ClickAt(x, y, button);
        return $"Clicked {button} button at ({x}, {y}) via {descriptor}";
    }

    private string DoubleClick(Dictionary<string, JsonElement> args)
    {
        if (!TryResolvePointFromArgs(args, out int x, out int y, out string descriptor, out string error))
            return error;

        _mouse.DoubleClick(x, y);
        return $"Double-clicked at ({x}, {y}) via {descriptor}";
    }

    private string Scroll(Dictionary<string, JsonElement> args)
    {
        int delta = DesktopToolDefinitions.GetInt(args, "delta", 3);
        _mouse.Scroll(delta);
        return $"Scrolled {(delta > 0 ? "up" : "down")} by {Math.Abs(delta)}";
    }

    private string TypeText(Dictionary<string, JsonElement> args)
    {
        string text = DesktopToolDefinitions.GetString(args, "text");

        if (LooksLikeSpecialInputText(text))
            return Err("Blocked type_text because the payload looks like special key or caret-navigation input. Use press_key for arrow keys, enter, return, tab, escape, backspace, delete, home, end, page up, or page down instead.");

        string normalizedText = NormalizeEscapedWhitespaceForTyping(text);
        _keyboard.TypeText(normalizedText);
        return $"Typed {normalizedText.Length} character(s)";
    }

    private static string NormalizeEscapedWhitespaceForTyping(string text)
    {
        if (string.IsNullOrEmpty(text) || !ContainsEscapedWhitespace(text))
            return text;

        if (LooksLikeLiteralEscapedText(text))
            return text;

        int escapedWhitespaceCount = Regex.Matches(text, @"(?<!\\)\\(?:r\\n|n|r|t)").Count;
        if (escapedWhitespaceCount == 1 && text.Length < 80)
            return text;

        return text
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
    }

    private static bool ContainsEscapedWhitespace(string text) =>
        Regex.IsMatch(text, @"(?<!\\)\\(?:r\\n|n|r|t)");

    private static bool LooksLikeLiteralEscapedText(string text)
    {
        if (text.Contains("```", StringComparison.Ordinal)
            || text.Contains("=>", StringComparison.Ordinal)
            || text.Contains("::", StringComparison.Ordinal)
            || text.Contains("</", StringComparison.Ordinal)
            || text.Contains("/>", StringComparison.Ordinal))
        {
            return true;
        }

        if (Regex.IsMatch(text, @"(?<!\\)\\[0abefvuxdDsSwW]"))
            return true;

        return Regex.IsMatch(text, @"\b(class|function|def|return|const|let|var|public|private|protected|SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeSpecialInputText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = Regex.Replace(text, @"[^\p{L}\p{N}]+", " ")
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (SpecialInputTextTokens.Contains(normalized) || SpecialInputTwoWordPhrases.Contains(normalized))
            return true;

        string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
            return false;

        for (int index = 0; index < tokens.Length;)
        {
            if (index + 1 < tokens.Length)
            {
                string twoWordPhrase = $"{tokens[index]} {tokens[index + 1]}";
                if (SpecialInputTwoWordPhrases.Contains(twoWordPhrase))
                {
                    index += 2;
                    continue;
                }
            }

            if (!SpecialInputTextTokens.Contains(tokens[index]))
                return false;

            index++;
        }

        return true;
    }

    private string PressKey(Dictionary<string, JsonElement> args)
    {
        string key = DesktopToolDefinitions.GetString(args, "key");
        _keyboard.PressKey(key);
        return $"Pressed key: {key}";
    }

    private string WordCreateDocument(Dictionary<string, JsonElement> args)
    {
        if (!OperatingSystem.IsMacOS())
            return Err("word_create_document is only supported on macOS.");

        string text = DesktopToolDefinitions.GetString(args, "text");
        bool activate = !args.ContainsKey("activate") || DesktopToolDefinitions.GetBool(args, "activate");

        try
        {
            string documentName = RunWordCreateDocumentScript(text, activate);
            return string.IsNullOrWhiteSpace(text)
                ? $"Created Microsoft Word document: {documentName}"
                : $"Created Microsoft Word document: {documentName}. Initial text length: {text.Length} characters.";
        }
        catch (Exception ex)
        {
            return Err($"Failed to create Word document: {ex.Message}");
        }
    }

    private string WordSetDocumentText(Dictionary<string, JsonElement> args)
    {
        if (!OperatingSystem.IsMacOS())
            return Err("word_set_document_text is only supported on macOS.");

        string text = DesktopToolDefinitions.GetString(args, "text");
        if (string.IsNullOrEmpty(text))
            return Err("text is required");

        string documentName = DesktopToolDefinitions.GetString(args, "document_name").Trim();
        bool append = DesktopToolDefinitions.GetBool(args, "append");
        bool activate = !args.ContainsKey("activate") || DesktopToolDefinitions.GetBool(args, "activate");

        try
        {
            string resolvedDocumentName = RunWordSetDocumentTextScript(documentName, text, append, activate);
            return append
                ? $"Appended plain text to Microsoft Word document: {resolvedDocumentName}. Added {text.Length} characters."
                : $"Set plain text in Microsoft Word document: {resolvedDocumentName}. New text length: {text.Length} characters.";
        }
        catch (Exception ex)
        {
            return Err($"Failed to set Word document text: {ex.Message}");
        }
    }

    private string WordReplaceText(Dictionary<string, JsonElement> args)
    {
        if (!OperatingSystem.IsMacOS())
            return Err("word_replace_text is only supported on macOS.");

        string searchText = DesktopToolDefinitions.GetString(args, "search_text");
        string replacementText = DesktopToolDefinitions.GetString(args, "replacement_text");
        string documentName = DesktopToolDefinitions.GetString(args, "document_name").Trim();
        bool replaceAll = DesktopToolDefinitions.GetBool(args, "replace_all");
        bool activate = !args.ContainsKey("activate") || DesktopToolDefinitions.GetBool(args, "activate");

        if (string.IsNullOrEmpty(searchText))
            return Err("search_text is required");

        try
        {
            string currentText = RunWordGetDocumentTextScript(documentName, activate, out string resolvedDocumentName);
            int occurrenceCount = CountOccurrences(currentText, searchText);
            if (occurrenceCount == 0)
                return Err($"The text '{searchText}' was not found in Microsoft Word document: {resolvedDocumentName}.");

            int replacements = replaceAll ? occurrenceCount : 1;
            string updatedText = replaceAll
                ? currentText.Replace(searchText, replacementText, StringComparison.Ordinal)
                : ReplaceFirstOccurrence(currentText, searchText, replacementText);

            RunWordSetDocumentTextScript(resolvedDocumentName, updatedText, append: false, activate: activate);
            return replaceAll
                ? $"Replaced {replacements} occurrence(s) of '{searchText}' in Microsoft Word document: {resolvedDocumentName}."
                : $"Replaced the first occurrence of '{searchText}' in Microsoft Word document: {resolvedDocumentName}.";
        }
        catch (Exception ex)
        {
            return Err($"Failed to replace Word document text: {ex.Message}");
        }
    }

    private string WordFormatText(Dictionary<string, JsonElement> args)
    {
        if (!OperatingSystem.IsMacOS())
            return Err("word_format_text is only supported on macOS.");

        string searchText = DesktopToolDefinitions.GetString(args, "search_text");
        string documentName = DesktopToolDefinitions.GetString(args, "document_name").Trim();
        bool activate = !args.ContainsKey("activate") || DesktopToolDefinitions.GetBool(args, "activate");

        bool hasBold = args.ContainsKey("bold");
        bool hasItalic = args.ContainsKey("italic");
        bool hasUnderline = args.ContainsKey("underline");

        if (string.IsNullOrWhiteSpace(searchText))
            return Err("search_text is required");

        if (!hasBold && !hasItalic && !hasUnderline)
            return Err("At least one formatting flag is required: bold, italic, or underline.");

        try
        {
            (string resolvedDocumentName, int formattedCount) = RunWordFormatTextScript(
                documentName,
                searchText,
                hasBold,
                DesktopToolDefinitions.GetBool(args, "bold"),
                hasItalic,
                DesktopToolDefinitions.GetBool(args, "italic"),
                hasUnderline,
                DesktopToolDefinitions.GetBool(args, "underline"),
                activate);

            if (formattedCount == 0)
                return Err($"The text '{searchText}' was not found in Microsoft Word document: {resolvedDocumentName}.");

            return $"Applied formatting to {formattedCount} matching word(s) in Microsoft Word document: {resolvedDocumentName}.";
        }
        catch (Exception ex)
        {
            return Err($"Failed to format Word document text: {ex.Message}");
        }
    }

    private string OpenApplication(Dictionary<string, JsonElement> args)
    {
        string name = DesktopToolDefinitions.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Err("Application name is required");

        if (name.IndexOfAny(new[] { '/', '\\', ';', '&', '|', '`', '$', '<', '>' }) >= 0)
            return Err($"Invalid application name: '{name}'");

        try
        {
            string resolvedName = ResolveApplicationName(name);

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = resolvedName,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", new[] { "-a", resolvedName })
                {
                    UseShellExecute = false,
                });

                BringMacOSApplicationToForeground(resolvedName);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = resolvedName,
                    UseShellExecute = true,
                });
            }

            return OperatingSystem.IsMacOS()
                ? $"Opened and activated application: {resolvedName}"
                : $"Opened application: {resolvedName}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to open '{name}': {ex.Message}");
        }
    }

    private string FocusApplication(Dictionary<string, JsonElement> args)
    {
        string name = DesktopToolDefinitions.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return Err("Application name is required");

        if (name.IndexOfAny(new[] { '/', '\\', ';', '&', '|', '`', '$', '<', '>' }) >= 0)
            return Err($"Invalid application name: '{name}'");

        try
        {
            string resolvedName = ResolveApplicationName(name);

            if (OperatingSystem.IsMacOS())
            {
                string verificationMessage = EnsureMacOSApplicationForeground(resolvedName);
                return $"Focused application: {resolvedName}. {verificationMessage}";
            }

            return Err($"Focusing applications by name is not implemented on this platform. Requested: {resolvedName}");
        }
        catch (Exception ex)
        {
            return Err($"Failed to focus '{name}': {ex.Message}");
        }
    }

    private string OpenUrl(Dictionary<string, JsonElement> args)
    {
        string url = DesktopToolDefinitions.GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Err("URL is required");

        if (!TryGetHttpUri(url, out Uri? parsedUri))
            return Err($"Invalid URL: '{url}'");

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", new[] { parsedUri.AbsoluteUri })
                {
                    UseShellExecute = false,
                });
            }
            else if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = parsedUri.AbsoluteUri,
                    UseShellExecute = true,
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", new[] { parsedUri.AbsoluteUri })
                {
                    UseShellExecute = false,
                });
            }

            return $"Opened URL: {parsedUri.AbsoluteUri}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to open URL '{parsedUri.AbsoluteUri}': {ex.Message}");
        }
    }

    private string RunCommand(Dictionary<string, JsonElement> args)
    {
        string command = DesktopToolDefinitions.GetString(args, "command");
        IReadOnlyList<string> arguments = DesktopToolDefinitions.GetStringArray(args, "arguments");
        int timeoutMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, TimeoutMsArg, 10_000), 100, 60_000);

        try
        {
            var result = _terminal.ExecuteCommand(command, arguments, timeoutMs);
            string prefix;
            if (result.TimedOut)
            {
                prefix = Err($"Command '{command}' timed out after {timeoutMs} ms.");
            }
            else if (result.ExitCode == 0)
            {
                prefix = $"Command '{command}' exited with code {result.ExitCode}.";
            }
            else
            {
                prefix = Err($"Command '{command}' exited with code {result.ExitCode}.");
            }

            return $"{prefix}\nSTDOUT:\n{FormatTerminalOutput(result.StandardOutput)}\nSTDERR:\n{FormatTerminalOutput(result.StandardError)}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to run command '{command}': {ex.Message}");
        }
    }

    private string ClickDockApplication(Dictionary<string, JsonElement> args)
    {
        IReadOnlyList<string> titles = GetTitles(args);
        if (titles.Count == 0)
            return Err("At least one Dock application title is required");

        try
        {
            _uiAutomation.ClickDockApplication(titles);
            return $"Clicked Dock application matching: {string.Join(", ", titles)}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to click Dock application: {ex.Message}");
        }
    }

    private string ClickAppleMenuItem(Dictionary<string, JsonElement> args)
    {
        IReadOnlyList<string> titles = GetTitles(args);
        if (titles.Count == 0)
            return Err("At least one Apple menu item title is required");

        try
        {
            _uiAutomation.ClickAppleMenuItem(titles);
            return $"Clicked Apple menu item matching: {string.Join(", ", titles)}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to click Apple menu item: {ex.Message}");
        }
    }

    private string ClickSystemSettingsSidebarItem(Dictionary<string, JsonElement> args)
    {
        IReadOnlyList<string> titles = GetTitles(args);
        if (titles.Count == 0)
            return Err("At least one System Settings sidebar title is required");

        try
        {
            _uiAutomation.ClickSystemSettingsSidebarItem(titles);
            return $"Clicked System Settings sidebar item matching: {string.Join(", ", titles)}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to click System Settings sidebar item: {ex.Message}");
        }
    }

    private string FocusWindow(Dictionary<string, JsonElement> args)
    {
        string applicationName = DesktopToolDefinitions.GetString(args, ApplicationNameArg).Trim();
        string titleContains = DesktopToolDefinitions.GetString(args, "title_contains").Trim();

        if (string.IsNullOrWhiteSpace(applicationName) && string.IsNullOrWhiteSpace(titleContains))
            return Err("application_name or title_contains is required");

        try
        {
            bool focused = _window.FocusWindow(
                string.IsNullOrWhiteSpace(applicationName) ? null : applicationName,
                string.IsNullOrWhiteSpace(titleContains) ? null : titleContains);

            return focused
                ? $"Focused window matching App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}"
                : Err($"No matching window found for App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}");
        }
        catch (Exception ex)
        {
            if (IsMacOSAccessibilityPermissionError(ex)
                && !string.IsNullOrWhiteSpace(applicationName)
                && string.IsNullOrWhiteSpace(titleContains))
            {
                try
                {
                    string verificationMessage = EnsureMacOSApplicationForeground(applicationName);
                    return $"Focused application fallback for {applicationName}. {verificationMessage}";
                }
                catch (Exception fallbackEx)
                {
                    return Err($"Failed to focus window and application fallback also failed: {fallbackEx.Message}");
                }
            }

            return Err($"Failed to focus window: {ex.Message}");
        }
    }

    private string WaitForWindow(Dictionary<string, JsonElement> args)
    {
        string applicationName = DesktopToolDefinitions.GetString(args, ApplicationNameArg).Trim();
        string titleContains = DesktopToolDefinitions.GetString(args, "title_contains").Trim();
        bool requireFrontmost = DesktopToolDefinitions.GetBool(args, "frontmost");
        bool absent = DesktopToolDefinitions.GetBool(args, "absent");
        int timeoutMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, TimeoutMsArg, 8_000), 100, 60_000);
        int pollIntervalMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, "poll_interval_ms", 250), 50, 5_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? fallbackDetails = null;
        while (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            try
            {
                IReadOnlyList<WindowInfo> windows = _window.ListWindows();
                bool matchExists = windows.Any(window => MatchesWindow(window, applicationName, titleContains, requireFrontmost));
                if ((absent && !matchExists) || (!absent && matchExists))
                {
                    return absent
                        ? $"Confirmed matching window is absent: App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}, Frontmost={requireFrontmost}"
                        : $"Matched window became available: App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}, Frontmost={requireFrontmost}";
                }
            }
            catch (Exception ex)
            {
                if (TryMatchWindowWithoutEnumeration(ex, applicationName, titleContains, requireFrontmost, absent, out string? fallbackResult, out string? details))
                {
                    if (fallbackResult is not null)
                        return fallbackResult;
                }

                if (details is not null)
                {
                    fallbackDetails = details;
                    Thread.Sleep(pollIntervalMs);
                    continue;
                }

                return Err($"Failed while waiting for window: {ex.Message}");
            }

            Thread.Sleep(pollIntervalMs);
        }

        return absent
            ? Err($"Timed out waiting for window to disappear: App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}, Frontmost={requireFrontmost}{FormatFallbackSuffix(fallbackDetails)}")
            : Err($"Timed out waiting for window: App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}, Frontmost={requireFrontmost}{FormatFallbackSuffix(fallbackDetails)}");
    }

    private string FocusFrontmostWindowContent(Dictionary<string, JsonElement> args)
    {
        string applicationName = DesktopToolDefinitions.GetString(args, ApplicationNameArg);

        try
        {
            return _uiAutomation.FocusFrontmostWindowContent(string.IsNullOrWhiteSpace(applicationName) ? null : applicationName);
        }
        catch (Exception ex)
        {
            if (TryFocusFrontmostWindowContentFallback(out string? fallbackResult))
                return $"{fallbackResult} AX focus error: {ex.Message}";

            return Err($"Failed to focus frontmost window content: {ex.Message}");
        }
    }

    private string FindUiElement(Dictionary<string, JsonElement> args)
    {
        string title = DesktopToolDefinitions.GetString(args, TitleArg).Trim();
        string role = DesktopToolDefinitions.GetString(args, "role").Trim();
        string value = DesktopToolDefinitions.GetString(args, ValueArg).Trim();

        try
        {
            IReadOnlyList<UiElementInfo> matches = _uiAutomation.FindFrontmostUiElements(
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(role) ? null : role,
                string.IsNullOrWhiteSpace(value) ? null : value);

            if (matches.Count == 0)
                return $"No matching UI elements found for Title={FormatWindowTitle(title)}, Role={FormatWindowTitle(role)}, Value={FormatWindowTitle(value)}";

            return string.Join(Environment.NewLine, matches.Select((element, index) => $"[{index}] {FormatUiElement(element)}"));
        }
        catch (Exception ex)
        {
            return Err($"Failed to find UI elements: {ex.Message}");
        }
    }

    private string ClickUiElement(Dictionary<string, JsonElement> args)
    {
        string title = DesktopToolDefinitions.GetString(args, TitleArg).Trim();
        string role = DesktopToolDefinitions.GetString(args, "role").Trim();
        string value = DesktopToolDefinitions.GetString(args, ValueArg).Trim();
        int matchIndex = Math.Max(0, DesktopToolDefinitions.GetInt(args, "match_index", 0));

        try
        {
            return _uiAutomation.ClickFrontmostUiElement(
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(role) ? null : role,
                string.IsNullOrWhiteSpace(value) ? null : value,
                matchIndex);
        }
        catch (Exception ex)
        {
            return Err($"Failed to click UI element: {ex.Message}");
        }
    }

    private string WaitForUiElement(Dictionary<string, JsonElement> args)
    {
        string title = DesktopToolDefinitions.GetString(args, TitleArg).Trim();
        string role = DesktopToolDefinitions.GetString(args, "role").Trim();
        string value = DesktopToolDefinitions.GetString(args, ValueArg).Trim();
        bool absent = DesktopToolDefinitions.GetBool(args, "absent");
        int timeoutMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, TimeoutMsArg, 8_000), 100, 60_000);
        int pollIntervalMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, "poll_interval_ms", 250), 50, 5_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            try
            {
                bool found = HasMatchingUiElement(title, role, value);

                if ((absent && !found) || (!absent && found))
                {
                    return absent
                        ? $"Confirmed matching UI element is absent: Title={FormatWindowTitle(title)}, Role={FormatWindowTitle(role)}, Value={FormatWindowTitle(value)}"
                        : $"Matched UI element became available: Title={FormatWindowTitle(title)}, Role={FormatWindowTitle(role)}, Value={FormatWindowTitle(value)}";
                }
            }
            catch (Exception ex)
            {
                return Err($"Failed while waiting for UI element: {ex.Message}");
            }

            Thread.Sleep(pollIntervalMs);
        }

        return absent
            ? Err($"Timed out waiting for UI element to disappear: Title={FormatWindowTitle(title)}, Role={FormatWindowTitle(role)}, Value={FormatWindowTitle(value)}")
            : Err($"Timed out waiting for UI element: Title={FormatWindowTitle(title)}, Role={FormatWindowTitle(role)}, Value={FormatWindowTitle(value)}");
    }

    private string GetFocusedUiElement()
    {
        try
        {
            UiElementInfo? element = _uiAutomation.GetFocusedUiElement();
            return element is null ? "No focused UI element found." : $"Focused UI element: {FormatUiElement(element.Value)}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to get focused UI element: {ex.Message}");
        }
    }

    private string AssertState(Dictionary<string, JsonElement> args)
    {
        string state = DesktopToolDefinitions.GetString(args, "state").Trim().ToLowerInvariant();
        bool expected = DesktopToolDefinitions.GetBool(args, "expected", true);
        string applicationName = DesktopToolDefinitions.GetString(args, ApplicationNameArg).Trim();
        string titleContains = DesktopToolDefinitions.GetString(args, "title_contains").Trim();
        string role = DesktopToolDefinitions.GetString(args, "role").Trim();
        string value = DesktopToolDefinitions.GetString(args, ValueArg).Trim();

        try
        {
            (bool actual, string details) = state switch
            {
                "frontmost_application" => AssertFrontmostApplication(applicationName),
                "window_present" => AssertWindowPresent(applicationName, titleContains),
                "ui_element_present" => AssertUiElementPresent(titleContains, role, value),
                "focused_ui_element" => AssertFocusedUiElement(titleContains, role, value),
                _ => throw new InvalidOperationException($"Unknown assert_state target: {state}")
            };

            bool passed = actual == expected;
            return passed
                ? $"Assertion passed: state={state}, expected={expected}, actual={actual}. {details}"
                : Err($"Assertion failed: state={state}, expected={expected}, actual={actual}. {details}");
        }
        catch (Exception ex)
        {
            return Err($"Failed to assert state '{state}': {ex.Message}");
        }
    }

    private bool TryFocusFrontmostWindowContentFallback([NotNullWhen(true)] out string? result)
    {
        result = null;

        try
        {
            WindowBounds bounds = _window.GetActiveWindowBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return false;

            int targetX = Math.Clamp(bounds.X + Math.Max(48, bounds.Width / 12), bounds.X + 10, bounds.X + bounds.Width - 10);
            int targetY = Math.Clamp(bounds.Y + Math.Max(140, bounds.Height / 3), bounds.Y + 10, bounds.Y + bounds.Height - 10);

            _mouse.ClickAt(targetX, targetY, MouseButton.Left);
            result = $"Focused frontmost window content via coordinate fallback at ({targetX}, {targetY}).";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool HasMatchingUiElement(string title, string role, string value)
        => _uiAutomation.FindFrontmostUiElements(
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(role) ? null : role,
            string.IsNullOrWhiteSpace(value) ? null : value).Count > 0;

    private string GetActiveWindowBounds()
    {
        try
        {
            WindowBounds bounds = _window.GetActiveWindowBounds();
            return $"Active window bounds: X={bounds.X}, Y={bounds.Y}, Width={bounds.Width}, Height={bounds.Height}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to get active window bounds: {ex.Message}");
        }
    }

    private string MoveActiveWindow(Dictionary<string, JsonElement> args)
    {
        int x = DesktopToolDefinitions.GetInt(args, "x");
        int y = DesktopToolDefinitions.GetInt(args, "y");

        try
        {
            _window.MoveActiveWindow(x, y);
            return $"Moved active window to ({x}, {y})";
        }
        catch (Exception ex)
        {
            return Err($"Failed to move active window: {ex.Message}");
        }
    }

    private string ResizeActiveWindow(Dictionary<string, JsonElement> args)
    {
        int width = Math.Max(100, DesktopToolDefinitions.GetInt(args, WidthArg));
        int height = Math.Max(100, DesktopToolDefinitions.GetInt(args, HeightArg));

        try
        {
            _window.ResizeActiveWindow(width, height);
            return $"Resized active window to {width}x{height}";
        }
        catch (Exception ex)
        {
            return Err($"Failed to resize active window: {ex.Message}");
        }
    }

    private static string Wait(Dictionary<string, JsonElement> args)
    {
        int milliseconds = Math.Clamp(DesktopToolDefinitions.GetInt(args, "milliseconds", 500), 100, 10_000);
        Thread.Sleep(milliseconds);
        return $"Waited {milliseconds} ms";
    }

    private static bool TryGetHttpUri(string url, [NotNullWhen(true)] out Uri? uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static string FormatTerminalOutput(string output)
        => string.IsNullOrWhiteSpace(output) ? "(none)" : output.TrimEnd();

    private static IReadOnlyList<string> GetTitles(Dictionary<string, JsonElement> args)
    {
        string title = DesktopToolDefinitions.GetString(args, "title");
        IReadOnlyList<string> alternateTitles = DesktopToolDefinitions.GetStringArray(args, "alternate_titles");

        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(title))
            titles.Add(title);

        titles.AddRange(alternateTitles.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)));
        return titles;
    }

    private static bool MatchesWindow(WindowInfo window, string applicationName, string titleContains, bool requireFrontmost)
    {
        if (!string.IsNullOrWhiteSpace(applicationName)
            && !window.ApplicationName.Contains(applicationName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(titleContains)
            && !window.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !requireFrontmost || window.IsFrontmost;
    }

    private static bool MatchesUiElement(UiElementInfo element, string title, string role, string value)
    {
        if (!string.IsNullOrWhiteSpace(title)
            && !element.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(role)
            && !element.Role.Contains(role, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(value)
            && !element.Value.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string FormatUiElement(UiElementInfo element)
    {
        string bounds = element.Bounds is WindowBounds windowBounds
            ? $", X={windowBounds.X}, Y={windowBounds.Y}, Width={windowBounds.Width}, Height={windowBounds.Height}"
            : string.Empty;
        return $"Role={FormatWindowTitle(element.Role)}, Title={FormatWindowTitle(element.Title)}, Value={FormatWindowTitle(element.Value)}, Focused={element.IsFocused}, Enabled={element.IsEnabled}{bounds}";
    }

    private bool TryDescribeWindowEnumerationFallback(Exception ex, [NotNullWhen(true)] out string? fallbackMessage)
    {
        fallbackMessage = null;
        if (!IsMacOSAccessibilityPermissionError(ex))
            return false;

        try
        {
            string frontmostApplication = _window.GetFrontmostApplicationName();
            fallbackMessage = $"Window listing unavailable because macOS Accessibility permission is missing for window enumeration. Frontmost application: {FormatWindowTitle(frontmostApplication)}. Use application-level focus or wait tools as a fallback until Accessibility access is restored.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryMatchWindowWithoutEnumeration(
        Exception ex,
        string applicationName,
        string titleContains,
        bool requireFrontmost,
        bool absent,
        out string? fallbackResult,
        out string? fallbackDetails)
    {
        fallbackResult = null;
        fallbackDetails = null;

        if (!IsMacOSAccessibilityPermissionError(ex))
            return false;

        if (!TryGetFrontmostWindowFallbackMatch(applicationName, titleContains, requireFrontmost, out bool matchExists, out string details))
            return false;

        fallbackDetails = details;
        if ((absent && !matchExists) || (!absent && matchExists))
        {
            fallbackResult = absent
                ? $"Confirmed matching window is absent: App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}, Frontmost={requireFrontmost}. {details}"
                : $"Matched window became available: App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}, Frontmost={requireFrontmost}. {details}";
        }

        return true;
    }

    private bool TryGetFrontmostWindowFallbackMatch(string applicationName, string titleContains, bool requireFrontmost, out bool matchExists, out string details)
    {
        matchExists = false;
        details = string.Empty;

        if (!string.IsNullOrWhiteSpace(titleContains))
            return false;

        if (!requireFrontmost && string.IsNullOrWhiteSpace(applicationName))
            return false;

        string frontmostApplication = _window.GetFrontmostApplicationName();
        matchExists = string.IsNullOrWhiteSpace(applicationName)
            ? !string.IsNullOrWhiteSpace(frontmostApplication)
            : ApplicationNamesMatch(frontmostApplication, applicationName);

        details = $"Fallback used frontmost application {FormatWindowTitle(frontmostApplication)} because macOS window enumeration is currently unavailable without Accessibility permission.";
        return true;
    }

    private static bool IsMacOSAccessibilityPermissionError(Exception ex)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        return MacOSAccessibilityPermissionErrorMarkers.Any(marker =>
            ex.Message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatFallbackSuffix(string? fallbackDetails)
        => string.IsNullOrWhiteSpace(fallbackDetails)
            ? string.Empty
            : $". {fallbackDetails}";

    private (bool Actual, string Details) AssertFrontmostApplication(string applicationName)
    {
        string frontmostApplication = _window.GetFrontmostApplicationName();
        bool actual = string.IsNullOrWhiteSpace(applicationName)
            ? !string.IsNullOrWhiteSpace(frontmostApplication)
            : frontmostApplication.Contains(applicationName, StringComparison.OrdinalIgnoreCase);
        return (actual, $"Frontmost application is {FormatWindowTitle(frontmostApplication)}.");
    }

    private (bool Actual, string Details) AssertWindowPresent(string applicationName, string titleContains)
    {
        WindowInfo? match = _window.ListWindows()
            .Where(window => MatchesWindow(window, applicationName, titleContains, requireFrontmost: false))
            .Select(static window => (WindowInfo?)window)
            .FirstOrDefault();
        return (match is not null, match is not null
            ? $"Matched window: App={FormatWindowTitle(match.Value.ApplicationName)}, Title={FormatWindowTitle(match.Value.Title)}."
            : $"No window matched App={FormatWindowTitle(applicationName)}, Title={FormatWindowTitle(titleContains)}.");
    }

    private (bool Actual, string Details) AssertUiElementPresent(string title, string role, string value)
    {
        IReadOnlyList<UiElementInfo> matches = _uiAutomation.FindFrontmostUiElements(
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(role) ? null : role,
            string.IsNullOrWhiteSpace(value) ? null : value);
        return (matches.Count > 0, matches.Count > 0
            ? $"Matched UI element: {FormatUiElement(matches[0])}"
            : $"No UI element matched Title={FormatWindowTitle(title)}, Role={FormatWindowTitle(role)}, Value={FormatWindowTitle(value)}.");
    }

    private (bool Actual, string Details) AssertFocusedUiElement(string title, string role, string value)
    {
        UiElementInfo? focused = _uiAutomation.GetFocusedUiElement();
        if (focused is null)
            return (false, "No focused UI element found.");

        bool actual = MatchesUiElement(focused.Value, title, role, value);
        return (actual, $"Focused UI element: {FormatUiElement(focused.Value)}");
    }

    internal static string ResolveApplicationName(string requestedName)
    {
        if (!OperatingSystem.IsMacOS())
            return requestedName;

        return requestedName.Trim() switch
        {
            var name when name.Equals("Word", StringComparison.OrdinalIgnoreCase) => "Microsoft Word",
            var name when name.Equals("Excel", StringComparison.OrdinalIgnoreCase) => "Microsoft Excel",
            var name when name.Equals("PowerPoint", StringComparison.OrdinalIgnoreCase) => "Microsoft PowerPoint",
            var name when name.Equals("Outlook", StringComparison.OrdinalIgnoreCase) => "Microsoft Outlook",
            _ => requestedName,
        };
    }

    private static void BringMacOSApplicationToForeground(string applicationName)
    {
        Thread.Sleep(700);

        string escapedApplicationName = applicationName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        string script = $$"""
        tell application "{{escapedApplicationName}}" to activate
        tell application "System Events"
            repeat with processItem in application processes
                if name of processItem is "{{escapedApplicationName}}" then
                    set frontmost of processItem to true
                    exit repeat
                end if
            end repeat
        end tell
        """;

        var psi = new System.Diagnostics.ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start osascript.");

        process.StandardInput.WriteLine(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Timed out while activating the application.");
        }

        if (process.ExitCode != 0)
        {
            string stderr = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Failed to activate the application." : stderr);
        }
    }

    private string EnsureMacOSApplicationForeground(string applicationName)
    {
        string? lastObservedFrontmostApplication = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            BringMacOSApplicationToForeground(applicationName);

            try
            {
                _window.FocusWindow(applicationName, null);
            }
            catch
            {
            }

            Thread.Sleep(250);

            try
            {
                lastObservedFrontmostApplication = _window.GetFrontmostApplicationName();
                if (ApplicationNamesMatch(lastObservedFrontmostApplication, applicationName))
                    return $"Foreground verification succeeded. Frontmost application: {lastObservedFrontmostApplication}.";
            }
            catch (Exception ex)
            {
                lastObservedFrontmostApplication = $"unavailable ({ex.Message})";
            }
        }

        throw new InvalidOperationException($"Activation did not make '{applicationName}' frontmost. Last observed frontmost application: {lastObservedFrontmostApplication ?? "unknown"}.");
    }

    internal static bool ApplicationNamesMatch(string? actualName, string expectedName)
    {
        if (string.IsNullOrWhiteSpace(actualName) || string.IsNullOrWhiteSpace(expectedName))
            return false;

        string normalizedActual = NormalizeApplicationName(actualName);
        string normalizedExpected = NormalizeApplicationName(expectedName);

        return normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase)
            || normalizedExpected.Contains(normalizedActual, StringComparison.OrdinalIgnoreCase);
    }

    internal static int CountOccurrences(string source, string value)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
            return 0;

        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    internal static string ReplaceFirstOccurrence(string source, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(oldValue))
            return source;

        int index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
            return source;

        return string.Concat(source.AsSpan(0, index), newValue, source.AsSpan(index + oldValue.Length));
    }

    internal static string BuildWordCreateDocumentScript()
        => """
        on run argv
            set requestedText to ""
            set shouldActivate to "true"
            if (count of argv) >= 1 then set requestedText to item 1 of argv
            if (count of argv) >= 2 then set shouldActivate to item 2 of argv

            tell application "Microsoft Word"
                if shouldActivate is "true" then activate
                set docRef to make new document
                if requestedText is not "" then
                    set content of text object of docRef to requestedText
                end if
                return name of docRef
            end tell
        end run
        """;

    internal static string BuildWordSetDocumentTextScript()
        => """
        on run argv
            set requestedDocumentName to item 1 of argv
            set requestedText to item 2 of argv
            set appendMode to item 3 of argv
            set shouldActivate to item 4 of argv

            tell application "Microsoft Word"
                if shouldActivate is "true" then activate
                if (count of documents) is 0 then error "No Microsoft Word document is open."

                if requestedDocumentName is "" then
                    set docRef to active document
                else
                    set matchingDocuments to every document whose name is requestedDocumentName
                    if (count of matchingDocuments) is 0 then error "No Microsoft Word document found named '" & requestedDocumentName & "'."
                    set docRef to item 1 of matchingDocuments
                end if

                if appendMode is "true" then
                    set content of text object of docRef to (content of text object of docRef) & requestedText
                else
                    set content of text object of docRef to requestedText
                end if

                return name of docRef
            end tell
        end run
        """;

    internal static string BuildWordGetDocumentTextScript()
        => """
        on run argv
            set requestedDocumentName to item 1 of argv
            set shouldActivate to item 2 of argv

            tell application "Microsoft Word"
                if shouldActivate is "true" then activate
                if (count of documents) is 0 then error "No Microsoft Word document is open."

                if requestedDocumentName is "" then
                    set docRef to active document
                else
                    set matchingDocuments to every document whose name is requestedDocumentName
                    if (count of matchingDocuments) is 0 then error "No Microsoft Word document found named '" & requestedDocumentName & "'."
                    set docRef to item 1 of matchingDocuments
                end if

                return (name of docRef) & linefeed & (content of text object of docRef)
            end tell
        end run
        """;

    internal static string BuildWordFormatTextScript()
        => """
        on run argv
            set requestedDocumentName to item 1 of argv
            set requestedSearchText to item 2 of argv
            set hasBoldFlag to item 3 of argv
            set boldValue to item 4 of argv
            set hasItalicFlag to item 5 of argv
            set italicValue to item 6 of argv
            set hasUnderlineFlag to item 7 of argv
            set underlineValue to item 8 of argv
            set shouldActivate to item 9 of argv

            tell application "Microsoft Word"
                if shouldActivate is "true" then activate
                if (count of documents) is 0 then error "No Microsoft Word document is open."

                if requestedDocumentName is "" then
                    set docRef to active document
                else
                    set matchingDocuments to every document whose name is requestedDocumentName
                    if (count of matchingDocuments) is 0 then error "No Microsoft Word document found named '" & requestedDocumentName & "'."
                    set docRef to item 1 of matchingDocuments
                end if

                set matchingWords to words of text object of docRef whose content contains requestedSearchText
                set formattedCount to count of matchingWords

                repeat with wordRange in matchingWords
                    if hasBoldFlag is "true" then set bold of font object of wordRange to (boldValue is "true")
                    if hasItalicFlag is "true" then set italic of font object of wordRange to (italicValue is "true")
                    if hasUnderlineFlag is "true" then
                        if underlineValue is "true" then
                            set underline of font object of wordRange to underline single
                        else
                            set underline of font object of wordRange to underline none
                        end if
                    end if
                end repeat

                return (name of docRef) & linefeed & formattedCount
            end tell
        end run
        """;

    private string RunWordCreateDocumentScript(string text, bool activate)
        => RunAppleScript(BuildWordCreateDocumentScript(), text, activate ? "true" : "false").Trim();

    private string RunWordSetDocumentTextScript(string documentName, string text, bool append, bool activate)
        => RunAppleScript(BuildWordSetDocumentTextScript(), documentName, text, append ? "true" : "false", activate ? "true" : "false").Trim();

    private string RunWordGetDocumentTextScript(string documentName, bool activate, out string resolvedDocumentName)
    {
        string output = RunAppleScript(BuildWordGetDocumentTextScript(), documentName, activate ? "true" : "false");
        string normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int lineBreakIndex = normalized.IndexOf('\n');
        if (lineBreakIndex < 0)
        {
            resolvedDocumentName = normalized.Trim();
            return string.Empty;
        }

        resolvedDocumentName = normalized[..lineBreakIndex].Trim();
        return normalized[(lineBreakIndex + 1)..];
    }

    private (string DocumentName, int FormattedCount) RunWordFormatTextScript(
        string documentName,
        string searchText,
        bool hasBold,
        bool bold,
        bool hasItalic,
        bool italic,
        bool hasUnderline,
        bool underline,
        bool activate)
    {
        string output = RunAppleScript(
            BuildWordFormatTextScript(),
            documentName,
            searchText,
            hasBold ? "true" : "false",
            bold ? "true" : "false",
            hasItalic ? "true" : "false",
            italic ? "true" : "false",
            hasUnderline ? "true" : "false",
            underline ? "true" : "false",
            activate ? "true" : "false");

        string normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        int lineBreakIndex = normalized.IndexOf('\n');
        if (lineBreakIndex < 0)
            throw new InvalidOperationException("Word format script returned an unexpected result.");

        string resolvedDocumentName = normalized[..lineBreakIndex].Trim();
        string countText = normalized[(lineBreakIndex + 1)..].Trim();
        if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int formattedCount))
            throw new InvalidOperationException("Word format script returned an invalid formatted-count value.");

        return (resolvedDocumentName, formattedCount);
    }

    private static string RunAppleScript(string script, params string[] arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("-");
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument ?? string.Empty);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start osascript.");

        process.StandardInput.Write(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Timed out while running AppleScript.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "AppleScript failed." : stderr);

        return stdout;
    }

    private static string NormalizeApplicationName(string applicationName)
    {
        string normalized = applicationName.Trim();

        if (normalized.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[10..];

        return normalized;
    }
}
