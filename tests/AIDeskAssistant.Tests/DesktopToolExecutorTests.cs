using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Tests;

internal sealed class FakeScreenshotService : IScreenshotService
{
    public ScreenshotCaptureOptions LastOptions;

    public byte[] TakeScreenshot(ScreenshotCaptureOptions options = default)
    {
        LastOptions = options;
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WnRsl0AAAAASUVORK5CYII=");
    }

    public ScreenInfo GetScreenInfo() => new(1920, 1080, 32);
}

internal sealed class FakeMouseService : IMouseService
{
    public (int X, int Y) LastMoveTarget;
    public (int X, int Y) LastClickTarget;
    public MouseButton LastClickButton;
    public (int X, int Y) LastDoubleClickTarget;
    public int LastScrollDelta;

    public void MoveTo(int x, int y) => LastMoveTarget = (x, y);

    public void Click(MouseButton button = MouseButton.Left)
    {
    }

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
    public string LastTypedText = string.Empty;
    public string LastPressedKey = string.Empty;

    public void TypeText(string text) => LastTypedText = text;

    public void PressKey(string keyCombo) => LastPressedKey = keyCombo;
}

internal sealed class FakeTerminalService : ITerminalService
{
    public string LastCommand = string.Empty;
    public IReadOnlyList<string> LastArguments = Array.Empty<string>();
    public int LastTimeoutMs;
    public (int ExitCode, string StandardOutput, string StandardError, bool TimedOut) NextResult = (0, "ok", string.Empty, false);

    public (int ExitCode, string StandardOutput, string StandardError, bool TimedOut) ExecuteCommand(string command, IReadOnlyList<string> arguments, int timeoutMs)
    {
        LastCommand = command;
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
    public string? FocusContentExceptionMessage;
    public string FrontmostUiSummary = "Frontmost app: TestApp\nVisible UI elements:\n- AXWindow | title=Main | x=10,y=20,w=800,h=600";

    public string SummarizeFrontmostUiElements() => FrontmostUiSummary;

    public void ClickDockApplication(IReadOnlyList<string> titles) => LastDockTitles = titles.ToArray();

    public void ClickAppleMenuItem(IReadOnlyList<string> titles) => LastAppleMenuTitles = titles.ToArray();

    public void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles) => LastSidebarTitles = titles.ToArray();

    public string FocusFrontmostWindowContent(string? applicationName)
    {
        LastFocusedContentApplicationName = applicationName;

        if (!string.IsNullOrWhiteSpace(FocusContentExceptionMessage))
            throw new InvalidOperationException(FocusContentExceptionMessage);

        return $"Focused frontmost window content for {applicationName ?? "<frontmost>"}";
    }
}

public sealed class DesktopToolExecutorTests
{
    private readonly FakeScreenshotService _screenshot = new();
    private readonly FakeMouseService _mouse = new();
    private readonly FakeKeyboardService _keyboard = new();
    private readonly FakeTerminalService _terminal = new();
    private readonly FakeWindowService _window = new();
    private readonly FakeUiAutomationService _uiAutomation = new();
    private readonly DesktopToolExecutor _sut;

    public DesktopToolExecutorTests()
    {
        _sut = new DesktopToolExecutor(_screenshot, _mouse, _keyboard, _terminal, _window, _uiAutomation);
    }

    [Fact]
    public void Execute_TakeScreenshot_ReturnsPayload()
    {
        string result = _sut.Execute("take_screenshot", "{}");

        Assert.Contains("Base64:", result);
        Assert.Contains("Capture bounds: X=0, Y=0, Width=1920, Height=1080.", result);
        Assert.Contains("Corner pixels: TL=(0,0), TR=(1919,0), BL=(0,1079), BR=(1919,1079).", result);
        Assert.Contains("Cursor: X=640, Y=480, InsideCapture=True.", result);
        Assert.Contains("Edge ruler: major ticks every", result);
        Assert.Contains("minor ticks every", result);
        Assert.Contains("Original:", result);
        Assert.Contains("Final:", result);
        Assert.Equal(default, _screenshot.LastOptions);
    }

    [Fact]
    public void Execute_TakeScreenshotActiveWindow_UsesWindowBoundsAndPurpose()
    {
        string result = _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"purpose\":\"verify word content\",\"padding\":20}");

        Assert.Equal(new WindowBounds(0, 0, 840, 640), _screenshot.LastOptions.Bounds);
        Assert.Contains("Target: active_window.", result);
        Assert.Contains("Purpose: verify word content.", result);
        Assert.Contains("Capture bounds: X=0, Y=0, Width=840, Height=640.", result);
        Assert.Contains("Corner pixels: TL=(0,0), TR=(839,0), BL=(0,639), BR=(839,639).", result);
        Assert.Contains("Cursor: X=640, Y=480, InsideCapture=True.", result);
        Assert.Contains("Likely content area: X=34, Y=90, Width=772, Height=518.", result);
        Assert.Contains("Edge ruler: major ticks every 50 px with minor ticks every 25 px.", result);
    }

    [Fact]
    public void Execute_MoveMouse_UsesMouseService()
    {
        string result = _sut.Execute("move_mouse", "{\"x\":300,\"y\":400}");

        Assert.Equal((300, 400), _mouse.LastMoveTarget);
        Assert.Equal("Mouse moved to (300, 400)", result);
    }

    [Fact]
    public void Execute_GetFrontmostUiElements_ReturnsUiAutomationSummary()
    {
        string result = _sut.Execute("get_frontmost_ui_elements", "{}");

        Assert.Contains("Frontmost app: TestApp", result);
        Assert.Contains("AXWindow | title=Main", result);
    }

    [Fact]
    public void Execute_Click_UsesSelectedButton()
    {
        string result = _sut.Execute("click", "{\"x\":100,\"y\":200,\"button\":\"right\"}");

        Assert.Equal((100, 200), _mouse.LastClickTarget);
        Assert.Equal(MouseButton.Right, _mouse.LastClickButton);
        Assert.Equal("Clicked Right button at (100, 200)", result);
    }

    [Fact]
    public void Execute_DoubleClick_UsesMouseService()
    {
        _sut.Execute("double_click", "{\"x\":150,\"y\":250}");

        Assert.Equal((150, 250), _mouse.LastDoubleClickTarget);
    }

    [Fact]
    public void Execute_Scroll_UsesMouseService()
    {
        string result = _sut.Execute("scroll", "{\"delta\":-5}");

        Assert.Equal(-5, _mouse.LastScrollDelta);
        Assert.Equal("Scrolled down by 5", result);
    }

    [Fact]
    public void Execute_TypeText_UsesKeyboardService()
    {
        string result = _sut.Execute("type_text", "{\"text\":\"Hello\"}");

        Assert.Equal("Hello", _keyboard.LastTypedText);
        Assert.Equal("Typed 5 character(s)", result);
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("right right right")]
    [InlineData("pageDown")]
    [InlineData("ENTER")]
    [InlineData("TAB")]
    [InlineData("ESC")]
    [InlineData("BACKSPACE")]
    public void Execute_TypeText_BlocksNavigationLikeInput(string text)
    {
        string result = _sut.Execute("type_text", $"{{\"text\":\"{text}\"}}");

        Assert.Equal(string.Empty, _keyboard.LastTypedText);
        Assert.Contains("Blocked type_text", result);
        Assert.Contains("Use press_key", result);
    }

    [Fact]
    public void Execute_PressKey_UsesKeyboardService()
    {
        string result = _sut.Execute("press_key", "{\"key\":\"cmd+s\"}");

        Assert.Equal("cmd+s", _keyboard.LastPressedKey);
        Assert.Equal("Pressed key: cmd+s", result);
    }

    [Fact]
    public void Execute_FocusFrontmostWindowContent_FallsBackToWindowClick_WhenUiAutomationFails()
    {
        _uiAutomation.FocusContentExceptionMessage = "AX focus failed";

        string result = _sut.Execute("focus_frontmost_window_content", "{\"application_name\":\"TextEdit\"}");

        Assert.Equal((76, 220), _mouse.LastClickTarget);
        Assert.Equal(MouseButton.Left, _mouse.LastClickButton);
        Assert.Contains("coordinate fallback", result);
        Assert.Contains("AX focus failed", result);
    }

    [Fact]
    public void Execute_RunCommand_FormatsTerminalResult()
    {
        _terminal.NextResult = (2, "stdout", "stderr", false);

        string result = _sut.Execute("run_command", "{\"command\":\"echo\",\"arguments\":[\"hello\"],\"timeout_ms\":5000}");

        Assert.Equal("echo", _terminal.LastCommand);
        Assert.Equal(new[] { "hello" }, _terminal.LastArguments);
        Assert.Equal(5000, _terminal.LastTimeoutMs);
        Assert.Contains("exited with code 2", result);
        Assert.Contains("stdout", result);
        Assert.Contains("stderr", result);
    }

    [Fact]
    public void Execute_RunCommand_ClampsTimeout()
    {
        _sut.Execute("run_command", "{\"command\":\"echo\",\"timeout_ms\":999999}");

        Assert.Equal(60000, _terminal.LastTimeoutMs);
    }

    [Fact]
    public void Execute_OpenApplication_RejectsUnsafeName()
    {
        string result = _sut.Execute("open_application", "{\"name\":\"bad;app\"}");

        Assert.Equal("Invalid application name: 'bad;app'", result);
    }

    [Fact]
    public void Execute_FocusApplication_RejectsUnsafeName()
    {
        string result = _sut.Execute("focus_application", "{\"name\":\"bad|app\"}");

        Assert.Equal("Invalid application name: 'bad|app'", result);
    }

    [Fact]
    public void Execute_OpenUrl_RejectsInvalidScheme()
    {
        string result = _sut.Execute("open_url", "{\"url\":\"file:///tmp/test\"}");

        Assert.Equal("Invalid URL: 'file:///tmp/test'", result);
    }

    [Fact]
    public void Execute_ClickDockApplication_ForwardsTitles()
    {
        string result = _sut.Execute("click_dock_application", "{\"title\":\"Safari\",\"alternate_titles\":[\"Web Browser\"]}");

        Assert.Equal(new[] { "Safari", "Web Browser" }, _uiAutomation.LastDockTitles);
        Assert.Contains("Safari, Web Browser", result);
    }

    [Fact]
    public void Execute_ClickAppleMenuItem_RequiresTitle()
    {
        string result = _sut.Execute("click_apple_menu_item", "{}");

        Assert.Equal("At least one Apple menu item title is required", result);
    }

    [Fact]
    public void Execute_ClickSystemSettingsSidebarItem_ForwardsTitles()
    {
        string result = _sut.Execute("click_system_settings_sidebar_item", "{\"title\":\"Displays\"}");

        Assert.Equal(new[] { "Displays" }, _uiAutomation.LastSidebarTitles);
        Assert.Contains("Displays", result);
    }

    [Fact]
    public void Execute_FocusFrontmostWindowContent_ForwardsApplicationName()
    {
        string result = _sut.Execute("focus_frontmost_window_content", "{\"application_name\":\"Safari\"}");

        Assert.Equal("Safari", _uiAutomation.LastFocusedContentApplicationName);
        Assert.Equal("Focused frontmost window content for Safari", result);
    }

    [Fact]
    public void Execute_GetActiveWindowBounds_UsesWindowService()
    {
        string result = _sut.Execute("get_active_window_bounds", "{}");

        Assert.Contains("X=10", result);
        Assert.Contains("Y=20", result);
        Assert.Contains("Width=800", result);
        Assert.Contains("Height=600", result);
    }

    [Fact]
    public void Execute_MoveActiveWindow_UsesWindowService()
    {
        string result = _sut.Execute("move_active_window", "{\"x\":42,\"y\":84}");

        Assert.Equal((42, 84), _window.LastMoveTarget);
        Assert.Equal("Moved active window to (42, 84)", result);
    }

    [Fact]
    public void Execute_ResizeActiveWindow_EnforcesMinimumSize()
    {
        string result = _sut.Execute("resize_active_window", "{\"width\":50,\"height\":80}");

        Assert.Equal((100, 100), _window.LastResizeTarget);
        Assert.Equal("Resized active window to 100x100", result);
    }

    [Fact]
    public void Execute_Wait_ClampsMilliseconds()
    {
        string result = _sut.Execute("wait", "{\"milliseconds\":10}");

        Assert.Equal("Waited 100 ms", result);
    }

    [Fact]
    public void Execute_UnknownTool_ReturnsError()
    {
        string result = _sut.Execute("does_not_exist", "{}");

        Assert.Equal("Unknown tool: does_not_exist", result);
    }

    [Theory]
    [InlineData("Word", "Microsoft Word")]
    [InlineData("Excel", "Microsoft Excel")]
    [InlineData("Notes", "Notes")]
    public void ResolveApplicationName_ReturnsExpectedMacAliases(string requested, string expected)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Equal(requested, DesktopToolExecutor.ResolveApplicationName(requested));
            return;
        }

        Assert.Equal(expected, DesktopToolExecutor.ResolveApplicationName(requested));
    }
}
