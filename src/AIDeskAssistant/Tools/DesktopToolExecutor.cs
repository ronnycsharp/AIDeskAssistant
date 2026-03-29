using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tools;

/// <summary>Executes desktop tool calls dispatched by the OpenAI model.</summary>
internal sealed class DesktopToolExecutor
{
    private readonly IScreenshotService _screenshot;
    private readonly IMouseService      _mouse;
    private readonly IKeyboardService   _keyboard;
    private readonly ITerminalService   _terminal;
    private readonly IWindowService     _window;

    public DesktopToolExecutor(
        IScreenshotService screenshot,
        IMouseService mouse,
        IKeyboardService keyboard,
        ITerminalService terminal,
        IWindowService window)
    {
        _screenshot = screenshot;
        _mouse      = mouse;
        _keyboard   = keyboard;
        _terminal   = terminal;
        _window     = window;
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
            "open_url"          => OpenUrl(args),
            "run_command"       => RunCommand(args),
            "get_active_window_bounds" => GetActiveWindowBounds(),
            "move_active_window" => MoveActiveWindow(args),
            "resize_active_window" => ResizeActiveWindow(args),
            "wait"              => Wait(args),
            _                   => $"Unknown tool: {toolName}",
        };
    }

    private string TakeScreenshot()
    {
        byte[] png    = _screenshot.TakeScreenshot();
        string base64 = Convert.ToBase64String(png);
        return $"Screenshot taken ({png.Length} bytes). Base64 PNG: {base64}";
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
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = name,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Pass app name as a separate argument to avoid shell injection.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "open", ["-a", name])
                {
                    UseShellExecute = false,
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = name,
                    UseShellExecute = true,
                });
            }
            return $"Opened application: {name}";
        }
        catch (Exception ex)
        {
            return $"Failed to open '{name}': {ex.Message}";
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
}
