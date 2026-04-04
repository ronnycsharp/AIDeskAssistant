using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tools;

/// <summary>Executes desktop tool calls dispatched by the OpenAI model.</summary>
internal sealed class DesktopToolExecutor
{
    private const string TimeoutMsArg = "timeout_ms";
    private const string WidthArg = "width";
    private const string HeightArg = "height";
    private const string FullScreenScreenshotTarget = "full_screen";
    private const string ActiveWindowScreenshotTarget = "active_window";
    private const string MouseDetailBase64Marker = "Mouse detail base64:";
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

    public DesktopToolExecutor(
        IScreenshotService screenshot,
        IMouseService mouse,
        IKeyboardService keyboard,
        ITerminalService terminal,
        IWindowService window,
        IUiAutomationService uiAutomation)
    {
        _screenshot = screenshot;
        _screenshotOptimizer = new ScreenshotOptimizer(ScreenshotOptimizer.ReadFromEnvironment());
        _mouse = mouse;
        _keyboard = keyboard;
        _terminal = terminal;
        _window = window;
        _uiAutomation = uiAutomation;
    }

    public string Execute(string toolName, string argsJson)
    {
        var args = DesktopToolDefinitions.ParseArgs(argsJson);

        return toolName switch
        {
            "take_screenshot" => TakeScreenshot(args),
            "get_screen_info" => GetScreenInfo(),
            "get_frontmost_ui_elements" => GetFrontmostUiElements(),
            "get_cursor_position" => GetCursorPosition(),
            "move_mouse" => MoveMouse(args),
            "drag" => Drag(args),
            "click" => Click(args),
            "double_click" => DoubleClick(args),
            "scroll" => Scroll(args),
            "type_text" => TypeText(args),
            "press_key" => PressKey(args),
            "open_application" => OpenApplication(args),
            "focus_application" => FocusApplication(args),
            "open_url" => OpenUrl(args),
            "run_command" => RunCommand(args),
            "click_dock_application" => ClickDockApplication(args),
            "click_apple_menu_item" => ClickAppleMenuItem(args),
            "click_system_settings_sidebar_item" => ClickSystemSettingsSidebarItem(args),
            "focus_frontmost_window_content" => FocusFrontmostWindowContent(args),
            "get_active_window_bounds" => GetActiveWindowBounds(),
            "move_active_window" => MoveActiveWindow(args),
            "resize_active_window" => ResizeActiveWindow(args),
            "wait" => Wait(args),
            _ => $"Unknown tool: {toolName}",
        };
    }

    private string TakeScreenshot(Dictionary<string, JsonElement> args)
    {
        string target = DesktopToolDefinitions.GetString(args, "target", FullScreenScreenshotTarget)
            .Trim()
            .ToLowerInvariant();
        string purpose = DesktopToolDefinitions.GetString(args, "purpose").Trim();
        int padding = Math.Clamp(DesktopToolDefinitions.GetInt(args, "padding", 16), 0, 200);

        if (!TryResolveScreenshotCaptureOptions(target, padding, out ScreenshotCaptureOptions options, out WindowBounds? bounds, out string error))
            return error;

        ScreenInfo screenInfo = _screenshot.GetScreenInfo();
        WindowBounds captureBounds = bounds ?? new WindowBounds(0, 0, screenInfo.Width, screenInfo.Height);
        WindowBounds? suggestedContentArea = string.Equals(target, ActiveWindowScreenshotTarget, StringComparison.Ordinal)
            ? ScreenshotAnnotationData.CreateSuggestedContentArea(captureBounds)
            : null;
        var (cursorX, cursorY) = _mouse.GetPosition();
        ScreenshotClickTarget? intendedClickTarget = TryGetIntendedClickTarget(args);
        ScreenshotHighlightedRegion? intendedElementRegion = TryGetIntendedElementRegion(args);
        WindowHitTestResult? windowUnderCursor = TryGetWindowUnderCursor(cursorX, cursorY);
        var annotation = new ScreenshotAnnotationData(captureBounds, cursorX, cursorY, suggestedContentArea, intendedClickTarget, intendedElementRegion);

        byte[] screenshot = _screenshot.TakeScreenshot(options);
        byte[] annotatedScreenshot = ScreenshotAnnotator.Annotate(screenshot, annotation);
        ScreenshotPayload payload = _screenshotOptimizer.Optimize(annotatedScreenshot);
        ScreenshotCursorDetail? mouseDetail = ScreenshotCursorDetailExtractor.Create(annotatedScreenshot, annotation);
        ScreenshotPayload? mouseDetailPayload = mouseDetail is null ? null : _screenshotOptimizer.Optimize(mouseDetail.Bytes);
        var context = new ScreenshotResultContext(target, purpose, captureBounds, cursorX, cursorY, suggestedContentArea, intendedClickTarget, intendedElementRegion, windowUnderCursor);
        return BuildScreenshotToolResult(payload, mouseDetailPayload, mouseDetail?.Bounds, context);
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
            error = $"Invalid screenshot target: '{target}'. Supported values: '{FullScreenScreenshotTarget}', '{ActiveWindowScreenshotTarget}'.";
            return false;
        }

        try
        {
            WindowBounds activeWindow = _window.GetActiveWindowBounds();
            WindowBounds clampedBounds = ClampWindowBounds(activeWindow, padding, _screenshot.GetScreenInfo());
            if (clampedBounds.Width <= 0 || clampedBounds.Height <= 0)
            {
                error = "Failed to capture active window screenshot: active window bounds are empty.";
                return false;
            }

            bounds = clampedBounds;
            options = new ScreenshotCaptureOptions(clampedBounds);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to capture active window screenshot: {ex.Message}";
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

    private static string BuildScreenshotToolResult(ScreenshotPayload payload, ScreenshotPayload? mouseDetailPayload, WindowBounds? mouseDetailBounds, ScreenshotResultContext context)
    {
        var annotation = new ScreenshotAnnotationData(context.CaptureBounds, context.CursorX, context.CursorY, context.SuggestedContentArea, context.IntendedClickTarget, context.IntendedElementRegion);
        var parts = new List<string>
        {
            "Screenshot taken.",
            $"Target: {context.Target}.",
        };

        if (!string.IsNullOrWhiteSpace(context.Purpose))
            parts.Add($"Purpose: {context.Purpose}.");

        parts.Add($"Capture bounds: X={context.CaptureBounds.X}, Y={context.CaptureBounds.Y}, Width={context.CaptureBounds.Width}, Height={context.CaptureBounds.Height}.");
        parts.Add($"Corner pixels: TL=({annotation.TopLeft.X},{annotation.TopLeft.Y}), TR=({annotation.TopRight.X},{annotation.TopRight.Y}), BL=({annotation.BottomLeft.X},{annotation.BottomLeft.Y}), BR=({annotation.BottomRight.X},{annotation.BottomRight.Y}).");
        parts.Add($"Cursor: X={context.CursorX}, Y={context.CursorY}, InsideCapture={annotation.CursorIsInsideCapture}.");
        if (context.WindowUnderCursor is WindowHitTestResult window)
            parts.Add($"Window under cursor: App={window.ApplicationName}, Title={FormatWindowTitle(window.Title)}, X={window.Bounds.X}, Y={window.Bounds.Y}, Width={window.Bounds.Width}, Height={window.Bounds.Height}.");
        if (annotation.HasSuggestedContentArea && annotation.SuggestedContentArea is WindowBounds contentArea)
            parts.Add($"Likely content area: X={contentArea.X}, Y={contentArea.Y}, Width={contentArea.Width}, Height={contentArea.Height}. Prefer clicks and typing inside this region unless the screenshot shows a more specific control elsewhere.");
        if (context.IntendedClickTarget is ScreenshotClickTarget clickTarget)
            parts.Add($"Intended click target: X={clickTarget.X}, Y={clickTarget.Y}, InsideCapture={annotation.IntendedClickIsInsideCapture}, Label={FormatWindowTitle(clickTarget.Label)}. Use the highlighted target marker to validate the click before you press click or double_click.");
        if (context.IntendedElementRegion is ScreenshotHighlightedRegion highlightedRegion)
            parts.Add($"Highlighted AX element: X={highlightedRegion.Bounds.X}, Y={highlightedRegion.Bounds.Y}, Width={highlightedRegion.Bounds.Width}, Height={highlightedRegion.Bounds.Height}, IntersectsCapture={annotation.IntendedElementRegionIntersectsCapture}, Label={FormatWindowTitle(highlightedRegion.Label)}. Use the extra outline to validate the intended AX/UI element before clicking.");
        if (mouseDetailBounds is WindowBounds detailBounds)
            parts.Add($"Mouse detail bounds: X={detailBounds.X}, Y={detailBounds.Y}, Width={detailBounds.Width}, Height={detailBounds.Height}. The additional mouse detail image includes a 100 px coordinate raster with x/y labels to validate exact cursor placement before clicking or double-clicking.");
        parts.Add($"Coordinate raster: major lines every {GetScreenshotRulerMajorStep(payload.Width, payload.Height)} px with minor lines every {GetScreenshotRulerMinorStep(payload.Width, payload.Height)} px.");

        parts.Add($"Original: {payload.OriginalByteCount} bytes.");
        parts.Add($"Final: {payload.FinalByteCount} bytes.");
        parts.Add($"Saved: {payload.BytesSaved} bytes ({payload.SavingsRatio:P1}).");
        parts.Add($"Resolution: {payload.Width}x{payload.Height}.");
        parts.Add($"Media type: {payload.MediaType}.");
        parts.Add($"Base64: {Convert.ToBase64String(payload.Bytes)}");

        if (mouseDetailPayload is not null)
        {
            parts.Add($"Mouse detail media type: {mouseDetailPayload.MediaType}.");
            parts.Add($"{MouseDetailBase64Marker} {Convert.ToBase64String(mouseDetailPayload.Bytes)}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatWindowTitle(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<untitled>" : value;

    private readonly record struct ScreenshotResultContext(
        string Target,
        string Purpose,
        WindowBounds CaptureBounds,
        int CursorX,
        int CursorY,
        WindowBounds? SuggestedContentArea,
        ScreenshotClickTarget? IntendedClickTarget,
        ScreenshotHighlightedRegion? IntendedElementRegion,
        WindowHitTestResult? WindowUnderCursor);

    private static int GetScreenshotRulerMajorStep(int width, int height)
    {
        int reference = Math.Min(width, height);
        if (reference >= 1800)
            return 200;

        if (reference >= 900)
            return 100;

        return 50;
    }

    private static int GetScreenshotRulerMinorStep(int width, int height)
        => Math.Max(GetScreenshotRulerMajorStep(width, height) / 2, 25);

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
            return $"Frontmost UI element summary unavailable: {ex.Message}";
        }
    }

    private string GetCursorPosition()
    {
        var (x, y) = _mouse.GetPosition();
        return $"Cursor position: ({x}, {y})";
    }

    private string MoveMouse(Dictionary<string, JsonElement> args)
    {
        int x = DesktopToolDefinitions.GetInt(args, "x");
        int y = DesktopToolDefinitions.GetInt(args, "y");
        _mouse.MoveTo(x, y);
        return $"Mouse moved to ({x}, {y})";
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
        int x = DesktopToolDefinitions.GetInt(args, "x");
        int y = DesktopToolDefinitions.GetInt(args, "y");
        string buttonName = DesktopToolDefinitions.GetString(args, "button", "left");
        MouseButton button = buttonName.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        _mouse.ClickAt(x, y, button);
        return $"Clicked {button} button at ({x}, {y})";
    }

    private string DoubleClick(Dictionary<string, JsonElement> args)
    {
        int x = DesktopToolDefinitions.GetInt(args, "x");
        int y = DesktopToolDefinitions.GetInt(args, "y");
        _mouse.DoubleClick(x, y);
        return $"Double-clicked at ({x}, {y})";
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
            return "Blocked type_text because the payload looks like special key or caret-navigation input. Use press_key for arrow keys, enter, return, tab, escape, backspace, delete, home, end, page up, or page down instead.";

        _keyboard.TypeText(text);
        return $"Typed {text.Length} character(s)";
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

    private string OpenApplication(Dictionary<string, JsonElement> args)
    {
        string name = DesktopToolDefinitions.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Application name is required";

        if (name.IndexOfAny(new[] { '/', '\\', ';', '&', '|', '`', '$', '<', '>' }) >= 0)
            return $"Invalid application name: '{name}'";

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
            return $"Failed to open '{name}': {ex.Message}";
        }
    }

    private static string FocusApplication(Dictionary<string, JsonElement> args)
    {
        string name = DesktopToolDefinitions.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Application name is required";

        if (name.IndexOfAny(new[] { '/', '\\', ';', '&', '|', '`', '$', '<', '>' }) >= 0)
            return $"Invalid application name: '{name}'";

        try
        {
            string resolvedName = ResolveApplicationName(name);

            if (OperatingSystem.IsMacOS())
            {
                BringMacOSApplicationToForeground(resolvedName);
                return $"Focused application: {resolvedName}";
            }

            return $"Focusing applications by name is not implemented on this platform. Requested: {resolvedName}";
        }
        catch (Exception ex)
        {
            return $"Failed to focus '{name}': {ex.Message}";
        }
    }

    private string OpenUrl(Dictionary<string, JsonElement> args)
    {
        string url = DesktopToolDefinitions.GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "URL is required";

        if (!TryGetHttpUri(url, out Uri? parsedUri))
            return $"Invalid URL: '{url}'";

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
            return $"Failed to open URL '{parsedUri.AbsoluteUri}': {ex.Message}";
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
            string prefix = result.TimedOut
                ? $"Command '{command}' timed out after {timeoutMs} ms."
                : $"Command '{command}' exited with code {result.ExitCode}.";

            return $"{prefix}\nSTDOUT:\n{FormatTerminalOutput(result.StandardOutput)}\nSTDERR:\n{FormatTerminalOutput(result.StandardError)}";
        }
        catch (Exception ex)
        {
            return $"Failed to run command '{command}': {ex.Message}";
        }
    }

    private string ClickDockApplication(Dictionary<string, JsonElement> args)
    {
        IReadOnlyList<string> titles = GetTitles(args);
        if (titles.Count == 0)
            return "At least one Dock application title is required";

        try
        {
            _uiAutomation.ClickDockApplication(titles);
            return $"Clicked Dock application matching: {string.Join(", ", titles)}";
        }
        catch (Exception ex)
        {
            return $"Failed to click Dock application: {ex.Message}";
        }
    }

    private string ClickAppleMenuItem(Dictionary<string, JsonElement> args)
    {
        IReadOnlyList<string> titles = GetTitles(args);
        if (titles.Count == 0)
            return "At least one Apple menu item title is required";

        try
        {
            _uiAutomation.ClickAppleMenuItem(titles);
            return $"Clicked Apple menu item matching: {string.Join(", ", titles)}";
        }
        catch (Exception ex)
        {
            return $"Failed to click Apple menu item: {ex.Message}";
        }
    }

    private string ClickSystemSettingsSidebarItem(Dictionary<string, JsonElement> args)
    {
        IReadOnlyList<string> titles = GetTitles(args);
        if (titles.Count == 0)
            return "At least one System Settings sidebar title is required";

        try
        {
            _uiAutomation.ClickSystemSettingsSidebarItem(titles);
            return $"Clicked System Settings sidebar item matching: {string.Join(", ", titles)}";
        }
        catch (Exception ex)
        {
            return $"Failed to click System Settings sidebar item: {ex.Message}";
        }
    }

    private string FocusFrontmostWindowContent(Dictionary<string, JsonElement> args)
    {
        string applicationName = DesktopToolDefinitions.GetString(args, "application_name");

        try
        {
            return _uiAutomation.FocusFrontmostWindowContent(string.IsNullOrWhiteSpace(applicationName) ? null : applicationName);
        }
        catch (Exception ex)
        {
            if (TryFocusFrontmostWindowContentFallback(out string? fallbackResult))
                return $"{fallbackResult} AX focus error: {ex.Message}";

            return $"Failed to focus frontmost window content: {ex.Message}";
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

    private string GetActiveWindowBounds()
    {
        try
        {
            WindowBounds bounds = _window.GetActiveWindowBounds();
            return $"Active window bounds: X={bounds.X}, Y={bounds.Y}, Width={bounds.Width}, Height={bounds.Height}";
        }
        catch (Exception ex)
        {
            return $"Failed to get active window bounds: {ex.Message}";
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
            return $"Failed to move active window: {ex.Message}";
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
            return $"Failed to resize active window: {ex.Message}";
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
}
