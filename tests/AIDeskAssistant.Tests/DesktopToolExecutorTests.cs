using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Tests;

// ── Fakes ────────────────────────────────────────────────────────────────────

internal sealed class FakeScreenshotService : IScreenshotService
{
    public byte[] TakeScreenshot() => [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
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
    public void PressKey(string key)  => LastPressedKey = key;
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class DesktopToolExecutorTests
{
    private readonly FakeScreenshotService _screenshot = new();
    private readonly FakeMouseService      _mouse      = new();
    private readonly FakeKeyboardService   _keyboard   = new();
    private readonly DesktopToolExecutor   _sut;

    public DesktopToolExecutorTests()
    {
        _sut = new DesktopToolExecutor(_screenshot, _mouse, _keyboard);
    }

    [Fact]
    public void Execute_TakeScreenshot_ReturnsBase64String()
    {
        string result = _sut.Execute("take_screenshot", "{}");
        Assert.Contains("Base64 PNG:", result);
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
    public void Execute_UnknownTool_ReturnsErrorMessage()
    {
        string result = _sut.Execute("nonexistent_tool", "{}");
        Assert.Contains("Unknown tool", result);
    }
}
