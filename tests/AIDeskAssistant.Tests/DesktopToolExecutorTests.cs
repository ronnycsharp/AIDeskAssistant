using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Tests;

// ── Fakes ────────────────────────────────────────────────────────────────────

internal sealed class FakeScreenshotService : IScreenshotService
{
    public byte[] TakeScreenshot() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WnRsl0AAAAASUVORK5CYII=");
    public ScreenInfo GetScreenInfo() => new ScreenInfo(1920, 1080, 32);
}

internal sealed class FakeMouseService : IMouseService
{
    public (int X, int Y) LastMoveTarget;
    public (int X, int Y) LastClickTarget;
    public MouseButton    LastClickButton;
    public (int X, int Y) LastDoubleClickTarget;
    public int            LastScrollDelta;

    public void MoveTo(int x, int y) => LastMoveTarget = (x, y);

    public void Click(MouseButton button = MouseButton.Left) { }

    public void ClickAt(int x, int y, MouseButton button = MouseButton.Left)
    {
        LastClickTarget = (x, y);
        LastClickButton = button;
    }

    public void DoubleClick(int x, int y) => LastDoubleClickTarget = (x, y);

    public void Scroll(int delta) => LastScrollDelta = delta;

    public (int X, int Y) GetPosition() => (640, 480);
}

internal sealed class FakeKeyboardService : IKeyboardService
{
    public string LastTypedText  = string.Empty;
    public string LastPressedKey = string.Empty;

    public void TypeText(string text) => LastTypedText  = text;
    public void PressKey(string keyCombo)  => LastPressedKey = keyCombo;
}

internal sealed class FakeTerminalService : ITerminalService
{
    public string LastCommand = string.Empty;
    public IReadOnlyList<string> LastArguments = Array.Empty<string>();
    public int LastTimeoutMs;
    public (int ExitCode, string StandardOutput, string StandardError, bool TimedOut) NextResult
        = (0, "ok", string.Empty, false);

    public (int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
        ExecuteCommand(string command, IReadOnlyList<string> arguments, int timeoutMs)
    {
        LastCommand   = command;
        LastArguments = arguments;
        LastTimeoutMs = timeoutMs;
        return NextResult;
    }
}

internal sealed class FakeWindowService : IWindowService
{
    public WindowBounds Bounds = new(10, 20, 800, 600);
    public (int X, int Y) LastMoveTarget;
    public (int Width, int Height) LastResizeTarget;

    public WindowBounds GetActiveWindowBounds() => Bounds;

    public void MoveActiveWindow(int x, int y) => LastMoveTarget = (x, y);

    public void ResizeActiveWindow(int width, int height) => LastResizeTarget = (width, height);
}

internal sealed class FakeUiAutomationService : IUiAutomationService
{
    public IReadOnlyList<string> LastDockTitles = Array.Empty<string>();
    public IReadOnlyList<string> LastAppleMenuTitles = Array.Empty<string>();
    public IReadOnlyList<string> LastSidebarTitles = Array.Empty<string>();
    public string? LastFocusedContentApplicationName;

    public void ClickDockApplication(IReadOnlyList<string> titles) => LastDockTitles = titles.ToArray();

    public void ClickAppleMenuItem(IReadOnlyList<string> titles) => LastAppleMenuTitles = titles.ToArray();

    public void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles) => LastSidebarTitles = titles.ToArray();

    public string FocusFrontmostWindowContent(string? applicationName)
    {
        LastFocusedContentApplicationName = applicationName;
        return $"Focused frontmost window content for {applicationName ?? "<frontmost>"}";
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class DesktopToolExecutorTests
{
    private readonly FakeScreenshotService _screenshot = new();
    private readonly FakeMouseService      _mouse      = new();
    private readonly FakeKeyboardService   _keyboard   = new();
    private readonly FakeTerminalService   _terminal   = new();
    private readonly FakeWindowService     _window     = new();
    private readonly FakeUiAutomationService _uiAutomation = new();
    private readonly DesktopToolExecutor   _sut;

    public DesktopToolExecutorTests()
    {
        _sut = new DesktopToolExecutor(_screenshot, _mouse, _keyboard, _terminal, _window, _uiAutomation);
    }

    [Fact]
    public void Execute_TakeScreenshot_ReturnsBase64String()
    {
        string result = _sut.Execute("take_screenshot", "{}");
        Assert.Contains("Base64:", result);
        Assert.Contains("Original:", result);
        Assert.Contains("Final:", result);
    }

    [Fact]
    public void Execute_GetScreenInfo_ReturnsResolution()
    {
        string result = _sut.Execute("get_screen_info", "{}");
        Assert.Contains("1920", result);
        Assert.Contains("1080", result);
    }

    [Fact]
    public void Execute_GetCursorPosition_ReturnsCursorCoords()
    {
        string result = _sut.Execute("get_cursor_position", "{}");
        Assert.Contains("640", result);
        Assert.Contains("480", result);
    }

    [Fact]
    public void Execute_MoveMouse_CallsServiceWithCorrectCoords()
    {
        _sut.Execute("move_mouse", """{"x":300,"y":400}""");
        Assert.Equal((300, 400), _mouse.LastMoveTarget);
    }

    [Fact]
    public void Execute_MoveMouse_ReturnsConfirmation()
    {
        string result = _sut.Execute("move_mouse", """{"x":300,"y":400}""");
        Assert.Contains("300", result);
        Assert.Contains("400", result);
    }

    [Fact]
    public void Execute_Click_LeftButton_CallsServiceCorrectly()
    {
        _sut.Execute("click", """{"x":100,"y":200,"button":"left"}""");
        Assert.Equal((100, 200), _mouse.LastClickTarget);
        Assert.Equal(MouseButton.Left, _mouse.LastClickButton);
    }

    [Fact]
    public void Execute_Click_RightButton_UsesRightButton()
    {
        _sut.Execute("click", """{"x":50,"y":75,"button":"right"}""");
        Assert.Equal(MouseButton.Right, _mouse.LastClickButton);
    }

    [Fact]
    public void Execute_DoubleClick_CallsServiceWithCorrectCoords()
    {
        _sut.Execute("double_click", """{"x":150,"y":250}""");
        Assert.Equal((150, 250), _mouse.LastDoubleClickTarget);
    }

    [Fact]
    public void Execute_Scroll_PositiveDelta_ScrollsUp()
    {
        _sut.Execute("scroll", """{"delta":3}""");
        Assert.Equal(3, _mouse.LastScrollDelta);
    }

    [Fact]
    public void Execute_Scroll_NegativeDelta_ScrollsDown()
    {
        _sut.Execute("scroll", """{"delta":-5}""");
        Assert.Equal(-5, _mouse.LastScrollDelta);
    }

    [Fact]
    public void Execute_TypeText_CallsServiceWithText()
    {
        _sut.Execute("type_text", """{"text":"Hello World"}""");
        Assert.Equal("Hello World", _keyboard.LastTypedText);
    }

    [Fact]
    public void Execute_TypeText_ReturnsCharacterCount()
    {
        string result = _sut.Execute("type_text", """{"text":"Hello"}""");
        Assert.Contains("5", result);
    }

    [Fact]
    public void Execute_PressKey_CallsServiceWithKey()
    {
        _sut.Execute("press_key", """{"key":"ctrl+c"}""");
        Assert.Equal("ctrl+c", _keyboard.LastPressedKey);
    }

    [Fact]
    public void Execute_Wait_ReturnsConfirmation()
    {
        string result = _sut.Execute("wait", """{"milliseconds":100}""");
        Assert.Contains("100", result);
    }

    [Fact]
    public void Execute_Wait_ClampsToMinimum()
    {
        string result = _sut.Execute("wait", """{"milliseconds":1}""");
        // Should clamp to 100 ms minimum
        Assert.Contains("100", result);
    }

    [Fact]
    public void Execute_Wait_ClampsToMaximum()
    {
        string result = _sut.Execute("wait", """{"milliseconds":99999}""");
        // Should clamp to 10000 ms maximum
        Assert.Contains("10000", result);
    }

    [Fact]
    public void Execute_OpenApplication_EmptyName_ReturnsError()
    {
        string result = _sut.Execute("open_application", """{"name":""}""");
        Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_FocusApplication_EmptyName_ReturnsError()
    {
        string result = _sut.Execute("focus_application", """{"name":""}""");
        Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Safari/../../etc/passwd")]
    [InlineData("app;rm -rf /")]
    [InlineData("app|evil")]
    public void Execute_OpenApplication_MaliciousName_ReturnsInvalidError(string name)
    {
        string json   = System.Text.Json.JsonSerializer.Serialize(new { name });
        string result = _sut.Execute("open_application", json);
        Assert.Contains("Invalid", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_OpenUrl_EmptyUrl_ReturnsError()
    {
        string result = _sut.Execute("open_url", """{"url":""}""");
        Assert.Contains("required", result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/test.txt")]
    [InlineData("javascript:alert('xss')")]
    public void Execute_OpenUrl_InvalidUrl_ReturnsInvalidError(string url)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { url });
        string result = _sut.Execute("open_url", json);
        Assert.Contains("Invalid URL", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_RunCommand_PassesCommandArgumentsAndTimeout()
    {
        _terminal.NextResult = (0, "On branch main", string.Empty, false);

        string result = _sut.Execute("run_command", """{"command":"git","arguments":["status","--short"],"timeout_ms":2500}""");

        Assert.Equal("git", _terminal.LastCommand);
        Assert.Equal(["status", "--short"], _terminal.LastArguments);
        Assert.Equal(2500, _terminal.LastTimeoutMs);
        Assert.Contains("On branch main", result);
    }

    [Fact]
    public void Execute_RunCommand_FormatsTimedOutResult()
    {
        _terminal.NextResult = (-1, "partial output", string.Empty, true);

        string result = _sut.Execute("run_command", """{"command":"dotnet"}""");

        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial output", result);
    }

    [Fact]
    public void Execute_PeekabooInspect_UsesConfiguredCommandAndArgs()
    {
        string? originalCommand = Environment.GetEnvironmentVariable("AIDESK_PEEKABOO_COMMAND");
        string? originalArgs = Environment.GetEnvironmentVariable("AIDESK_PEEKABOO_INSPECT_ARGUMENTS");
        string? originalTimeout = Environment.GetEnvironmentVariable("AIDESK_PEEKABOO_TIMEOUT_MS");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_PEEKABOO_COMMAND", "peekaboo-cli");
            Environment.SetEnvironmentVariable("AIDESK_PEEKABOO_INSPECT_ARGUMENTS", "see --json --app frontmost");
            Environment.SetEnvironmentVariable("AIDESK_PEEKABOO_TIMEOUT_MS", "25000");
            _terminal.NextResult = (0, "{\"ok\":true}", string.Empty, false);

            string result = _sut.Execute("peekaboo_inspect", """{"arguments":["--focused-only"]}""");

            Assert.Equal("peekaboo-cli", _terminal.LastCommand);
            Assert.Equal(["see", "--json", "--app", "frontmost", "--focused-only"], _terminal.LastArguments);
            Assert.Equal(25000, _terminal.LastTimeoutMs);
            Assert.Contains("Peekaboo command exited with code 0.", result);
            Assert.Contains("{\"ok\":true}", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_PEEKABOO_COMMAND", originalCommand);
            Environment.SetEnvironmentVariable("AIDESK_PEEKABOO_INSPECT_ARGUMENTS", originalArgs);
            Environment.SetEnvironmentVariable("AIDESK_PEEKABOO_TIMEOUT_MS", originalTimeout);
        }
    }

    [Fact]
    public void TokenizeArguments_PreservesQuotedSegments()
    {
        IReadOnlyList<string> tokens = DesktopToolExecutor.TokenizeArguments("see --json --app frontmost --query \"Microsoft Word\"");

        Assert.Equal(["see", "--json", "--app", "frontmost", "--query", "Microsoft Word"], tokens);
    }

    [Fact]
    public void Execute_GetActiveWindowBounds_ReturnsBounds()
    {
        string result = _sut.Execute("get_active_window_bounds", "{}");

        Assert.Contains("X=10", result);
        Assert.Contains("Y=20", result);
        Assert.Contains("Width=800", result);
        Assert.Contains("Height=600", result);
    }

    [Fact]
    public void Execute_MoveActiveWindow_CallsWindowService()
    {
        string result = _sut.Execute("move_active_window", """{"x":320,"y":180}""");

        Assert.Equal((320, 180), _window.LastMoveTarget);
        Assert.Contains("320", result);
        Assert.Contains("180", result);
    }

    [Fact]
    public void Execute_ResizeActiveWindow_CallsWindowService()
    {
        string result = _sut.Execute("resize_active_window", """{"width":1280,"height":720}""");

        Assert.Equal((1280, 720), _window.LastResizeTarget);
        Assert.Contains("1280", result);
        Assert.Contains("720", result);
    }

    [Fact]
    public void Execute_ResizeActiveWindow_ClampsToMinimum()
    {
        _sut.Execute("resize_active_window", """{"width":1,"height":50}""");

        Assert.Equal((100, 100), _window.LastResizeTarget);
    }

    [Fact]
    public void Execute_UnknownTool_ReturnsErrorMessage()
    {
        string result = _sut.Execute("nonexistent_tool", "{}");
        Assert.Contains("Unknown tool", result);
    }

    [Fact]
    public void Execute_ClickAppleMenuItem_CallsUiAutomationService()
    {
        string result = _sut.Execute("click_apple_menu_item", """{"title":"System Settings","alternate_titles":["System Preferences"]}""");

        Assert.Equal(["System Settings", "System Preferences"], _uiAutomation.LastAppleMenuTitles);
        Assert.Contains("System Settings", result);
    }

    [Fact]
    public void Execute_ClickDockApplication_CallsUiAutomationService()
    {
        string result = _sut.Execute("click_dock_application", """{"title":"Microsoft Word","alternate_titles":["Word"]}""");

        Assert.Equal(["Microsoft Word", "Word"], _uiAutomation.LastDockTitles);
        Assert.Contains("Microsoft Word", result);
    }

    [Fact]
    public void Execute_ClickSystemSettingsSidebarItem_CallsUiAutomationService()
    {
        string result = _sut.Execute("click_system_settings_sidebar_item", """{"title":"Wi-Fi","alternate_titles":["WLAN"]}""");

        Assert.Equal(["Wi-Fi", "WLAN"], _uiAutomation.LastSidebarTitles);
        Assert.Contains("Wi-Fi", result);
    }

    [Fact]
    public void Execute_FocusFrontmostWindowContent_CallsUiAutomationService()
    {
        string result = _sut.Execute("focus_frontmost_window_content", """{"application_name":"Microsoft Word"}""");

        Assert.Equal("Microsoft Word", _uiAutomation.LastFocusedContentApplicationName);
        Assert.Contains("Microsoft Word", result);
    }
}
