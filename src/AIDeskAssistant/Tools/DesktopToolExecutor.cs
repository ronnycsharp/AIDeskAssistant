using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tools;

/// <summary>Executes desktop tool calls dispatched by the OpenAI model.</summary>
internal sealed class DesktopToolExecutor
{
    private readonly IScreenshotService _screenshot;
    private readonly ScreenshotOptimizer _screenshotOptimizer;
    private readonly IMouseService      _mouse;
    private readonly IKeyboardService   _keyboard;
    private readonly ITerminalService   _terminal;
    private readonly IWindowService     _window;
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
        _mouse      = mouse;
        _keyboard   = keyboard;
        _terminal   = terminal;
        _window     = window;
        _uiAutomation = uiAutomation;
    }

    /// <summary>
    /// Executes the named tool with the supplied JSON arguments string and
    /// returns a plain-text result that will be fed back to the model.
    /// </summary>
    public string Execute(string toolName, string argsJson)
    {
        var args = DesktopToolDefinitions.ParseArgs(argsJson);

        return toolName switch
        {
            "take_screenshot"   => TakeScreenshot(),
            "get_screen_info"   => GetScreenInfo(),
            "get_cursor_position" => GetCursorPosition(),
            "move_mouse"        => MoveMouse(args),
            "click"             => Click(args),
            "double_click"      => DoubleClick(args),
            "scroll"            => Scroll(args),
            "type_text"         => TypeText(args),
            "press_key"         => PressKey(args),
            "open_application"  => OpenApplication(args),
            "focus_application" => FocusApplication(args),
            "open_url"          => OpenUrl(args),
            "run_command"       => RunCommand(args),
            "peekaboo_inspect"  => PeekabooInspect(args),
            "click_dock_application" => ClickDockApplication(args),
            "click_apple_menu_item" => ClickAppleMenuItem(args),
            "click_system_settings_sidebar_item" => ClickSystemSettingsSidebarItem(args),
            "focus_frontmost_window_content" => FocusFrontmostWindowContent(args),
            "get_active_window_bounds" => GetActiveWindowBounds(),
            "move_active_window" => MoveActiveWindow(args),
            "resize_active_window" => ResizeActiveWindow(args),
            "wait"              => Wait(args),
            _                   => $"Unknown tool: {toolName}",
        };
    }

    private string TakeScreenshot()
    {
        byte[] screenshot = _screenshot.TakeScreenshot();
        ScreenshotPayload payload = _screenshotOptimizer.Optimize(screenshot);
        return payload.ToToolResultString();
    }

    private string GetScreenInfo()
    {
        ScreenInfo info = _screenshot.GetScreenInfo();
        return $"Screen: {info.Width}x{info.Height}, {info.Depth} bpp";
    }

    private string GetCursorPosition()
    {
        var (x, y) = _mouse.GetPosition();
        return $"Cursor position: ({x}, {y})";
    }

    private string MoveMouse(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        int x = DesktopToolDefinitions.GetInt(args, "x");
        int y = DesktopToolDefinitions.GetInt(args, "y");
        _mouse.MoveTo(x, y);
        return $"Mouse moved to ({x}, {y})";
    }

    private string Click(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        int x      = DesktopToolDefinitions.GetInt(args, "x");
        int y      = DesktopToolDefinitions.GetInt(args, "y");
        string btn = DesktopToolDefinitions.GetString(args, "button", "left");
        MouseButton button = btn.ToLowerInvariant() switch
        {
            "right"  => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _        => MouseButton.Left,
        };
        _mouse.ClickAt(x, y, button);
        return $"Clicked {button} button at ({x}, {y})";
    }

    private string DoubleClick(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        int x = DesktopToolDefinitions.GetInt(args, "x");
        int y = DesktopToolDefinitions.GetInt(args, "y");
        _mouse.DoubleClick(x, y);
        return $"Double-clicked at ({x}, {y})";
    }

    private string Scroll(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        int delta = DesktopToolDefinitions.GetInt(args, "delta", 3);
        _mouse.Scroll(delta);
        return $"Scrolled {(delta > 0 ? "up" : "down")} by {Math.Abs(delta)}";
    }

    private string TypeText(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        string text = DesktopToolDefinitions.GetString(args, "text");
        _keyboard.TypeText(text);
        return $"Typed {text.Length} character(s)";
    }

    private string PressKey(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        string key = DesktopToolDefinitions.GetString(args, "key");
        _keyboard.PressKey(key);
        return $"Pressed key: {key}";
    }

    private string OpenApplication(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        string name = DesktopToolDefinitions.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Application name is required";

        // Basic validation: reject names containing shell metacharacters or path separators.
        if (name.IndexOfAny(['/', '\\', ';', '&', '|', '`', '$', '<', '>']) >= 0)
            return $"Invalid application name: '{name}'";

        try
        {
            string resolvedName = ResolveApplicationName(name);

            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = resolvedName,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "open", ["-a", resolvedName])
                {
                    UseShellExecute = false,
                });

                BringMacOSApplicationToForeground(resolvedName);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = resolvedName,
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

    private string FocusApplication(Dictionary<string, JsonElement> args)
    {
        string name = DesktopToolDefinitions.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Application name is required";

        if (name.IndexOfAny(['/', '\\', ';', '&', '|', '`', '$', '<', '>']) >= 0)
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

    private static string OpenUrl(Dictionary<string, JsonElement> args)
    {
        string url = DesktopToolDefinitions.GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "URL is required";

        if (!TryGetHttpUri(url, out Uri? parsedUri))
        {
            return $"Invalid URL: '{url}'";
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "open", [parsedUri.AbsoluteUri])
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
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "xdg-open", [parsedUri.AbsoluteUri])
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
        int timeoutMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, "timeout_ms", 10_000), 100, 60_000);

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

    private string PeekabooInspect(Dictionary<string, JsonElement> args)
    {
        PeekabooConfiguration config = ReadPeekabooConfiguration();
        IReadOnlyList<string> extraArguments = DesktopToolDefinitions.GetStringArray(args, "arguments");
        int timeoutMs = Math.Clamp(DesktopToolDefinitions.GetInt(args, "timeout_ms", config.TimeoutMs), 100, 120_000);

        List<string> commandArguments = [.. config.InspectArguments, .. extraArguments];

        try
        {
            var result = _terminal.ExecuteCommand(config.Command, commandArguments, timeoutMs);
            string prefix = result.TimedOut
                ? $"Peekaboo command timed out after {timeoutMs} ms."
                : $"Peekaboo command exited with code {result.ExitCode}.";

            return $"{prefix}\nCommand: {config.Command}\nArguments: {string.Join(" ", commandArguments)}\nSTDOUT:\n{FormatTerminalOutput(result.StandardOutput)}\nSTDERR:\n{FormatTerminalOutput(result.StandardError)}";
        }
        catch (Exception ex)
        {
            return $"Failed to run Peekaboo command '{config.Command}'. Configure AIDESK_PEEKABOO_COMMAND and optionally AIDESK_PEEKABOO_INSPECT_ARGUMENTS for your local installation. Error: {ex.Message}";
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
            return $"Failed to focus frontmost window content: {ex.Message}";
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
        int width  = Math.Max(100, DesktopToolDefinitions.GetInt(args, "width"));
        int height = Math.Max(100, DesktopToolDefinitions.GetInt(args, "height"));

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

    private static string Wait(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        int ms = Math.Clamp(DesktopToolDefinitions.GetInt(args, "milliseconds", 500), 100, 10_000);
        Thread.Sleep(ms);
        return $"Waited {ms} ms";
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

        titles.AddRange(alternateTitles.Where(static value => !string.IsNullOrWhiteSpace(value)));
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

    internal static PeekabooConfiguration ReadPeekabooConfiguration()
    {
        string command = Environment.GetEnvironmentVariable("AIDESK_PEEKABOO_COMMAND") ?? "peekaboo";
        string inspectArgumentsValue = Environment.GetEnvironmentVariable("AIDESK_PEEKABOO_INSPECT_ARGUMENTS") ?? "see --json --app frontmost --annotate";
        int timeoutMs = int.TryParse(Environment.GetEnvironmentVariable("AIDESK_PEEKABOO_TIMEOUT_MS"), out int parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 15_000;

        IReadOnlyList<string> inspectArguments = TokenizeArguments(inspectArgumentsValue);
        if (inspectArguments.Count == 0)
            inspectArguments = ["see", "--json", "--app", "frontmost", "--annotate"];

        return new PeekabooConfiguration(command, inspectArguments, timeoutMs);
    }

    internal static IReadOnlyList<string> TokenizeArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;

        foreach (char ch in value)
        {
            if (quote is null && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                if (quote is null)
                {
                    quote = ch;
                    continue;
                }

                if (quote == ch)
                {
                    quote = null;
                    continue;
                }
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}

internal sealed record PeekabooConfiguration(string Command, IReadOnlyList<string> InspectArguments, int TimeoutMs);
