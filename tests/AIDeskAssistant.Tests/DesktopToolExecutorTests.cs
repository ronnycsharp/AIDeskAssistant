using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;
using SkiaSharp;

namespace AIDeskAssistant.Tests;

internal sealed class FakeScreenshotService : IScreenshotService
{
    public ScreenshotCaptureOptions LastOptions;

    public byte[] TakeScreenshot(ScreenshotCaptureOptions options = default)
    {
        LastOptions = options;
        WindowBounds bounds = options.Bounds ?? new WindowBounds(0, 0, 1920, 1080);
        using var surface = SKSurface.Create(new SKImageInfo(bounds.Width, bounds.Height));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    public ScreenInfo GetScreenInfo() => new(1920, 1080, 32);
}

internal sealed class FakeMouseService : IMouseService
{
    public (int X, int Y) LastMoveTarget;
    public (int StartX, int StartY, int EndX, int EndY) LastDragTarget;
    public MouseButton LastDragButton;
    public (int X, int Y) LastClickTarget;
    public MouseButton LastClickButton;
    public (int X, int Y) LastDoubleClickTarget;
    public int LastScrollDelta;

    public void MoveTo(int x, int y) => LastMoveTarget = (x, y);

    public void Drag(int startX, int startY, int endX, int endY, MouseButton button = MouseButton.Left)
    {
        LastDragTarget = (startX, startY, endX, endY);
        LastDragButton = button;
    }

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

internal sealed class FakeTextRecognitionService : ITextRecognitionService
{
    public TextRecognitionResult NextResult = new(
        "1\n2\n3",
        [
            new TextRecognitionLine("1", 0.99, new WindowBounds(4, 6, 12, 16)),
            new TextRecognitionLine("2", 0.99, new WindowBounds(4, 26, 12, 16)),
            new TextRecognitionLine("3", 0.99, new WindowBounds(4, 46, 12, 16)),
        ]);

    public byte[] LastImageBytes = Array.Empty<byte>();

    public TextRecognitionResult RecognizeText(byte[] imageBytes)
    {
        LastImageBytes = imageBytes;
        return NextResult;
    }
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
    public WindowHitTestResult? WindowAtPoint = new("Microsoft Word", "Document1", new WindowBounds(0, 0, 840, 640));
    public string FrontmostApplicationName = "Microsoft Word";
    public IReadOnlyList<WindowInfo> Windows =
    [
        new WindowInfo("Microsoft Word", "Document1", new WindowBounds(10, 20, 800, 600), true, false),
        new WindowInfo("Finder", "Downloads", new WindowBounds(100, 80, 720, 540), false, false),
    ];
    public (int X, int Y) LastMoveTarget;
    public (int Width, int Height) LastResizeTarget;
    public string? LastFocusApplicationName;
    public string? LastFocusTitleSubstring;
    public bool FocusWindowResult = true;
    public int FocusWindowCallCount;
    public string? ListWindowsExceptionMessage;
    public string GetFrontmostApplicationExceptionMessage = string.Empty;

    public WindowBounds GetActiveWindowBounds() => Bounds;

    public WindowHitTestResult? GetWindowAtPoint(int x, int y) => WindowAtPoint;

    public string GetFrontmostApplicationName()
    {
        if (!string.IsNullOrWhiteSpace(GetFrontmostApplicationExceptionMessage))
            throw new InvalidOperationException(GetFrontmostApplicationExceptionMessage);

        return FrontmostApplicationName;
    }

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        if (!string.IsNullOrWhiteSpace(ListWindowsExceptionMessage))
            throw new InvalidOperationException(ListWindowsExceptionMessage);

        return Windows;
    }

    public bool FocusWindow(string? applicationName, string? titleSubstring)
    {
        FocusWindowCallCount++;
        LastFocusApplicationName = applicationName;
        LastFocusTitleSubstring = titleSubstring;
        return FocusWindowResult;
    }

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
    public string? LastFindTitle;
    public string? LastFindRole;
    public string? LastFindValue;
    public string? LastClickTitle;
    public string? LastClickRole;
    public string? LastClickValue;
    public int LastClickMatchIndex;
    public string FrontmostUiSummary = "Frontmost app: TestApp\nVisible UI elements:\n- AXWindow | title=Main | x=10,y=20,w=800,h=600";
    public IReadOnlyList<UiElementInfo> MatchingElements =
    [
        new UiElementInfo("AXButton", "Save", string.Empty, new WindowBounds(420, 300, 80, 28), false, true),
        new UiElementInfo("AXTextField", "Filename", "poem.docx", new WindowBounds(320, 240, 240, 24), true, true),
    ];
    public UiElementInfo? FocusedElement = new("AXTextField", "Filename", "poem.docx", new WindowBounds(320, 240, 240, 24), true, true);
    public string ClickUiElementResult = "Clicked UI element: role=AXButton, title=Save, value=, x=420, y=300, width=80, height=28";

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

    public IReadOnlyList<UiElementInfo> FindFrontmostUiElements(string? title = null, string? role = null, string? value = null)
    {
        LastFindTitle = title;
        LastFindRole = role;
        LastFindValue = value;

        return MatchingElements
            .Where(element => string.IsNullOrWhiteSpace(title)
                || (!string.IsNullOrWhiteSpace(element.Title)
                    && element.Title.Contains(title, StringComparison.OrdinalIgnoreCase)))
            .Where(element => string.IsNullOrWhiteSpace(role)
                || (!string.IsNullOrWhiteSpace(element.Role)
                    && element.Role.Contains(role, StringComparison.OrdinalIgnoreCase)))
            .Where(element => string.IsNullOrWhiteSpace(value)
                || (!string.IsNullOrWhiteSpace(element.Value)
                    && element.Value.Contains(value, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public UiElementInfo? GetFocusedUiElement() => FocusedElement;

    public string ClickFrontmostUiElement(string? title = null, string? role = null, string? value = null, int matchIndex = 0)
    {
        LastClickTitle = title;
        LastClickRole = role;
        LastClickValue = value;
        LastClickMatchIndex = matchIndex;
        return ClickUiElementResult;
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
    private readonly FakeTextRecognitionService _textRecognition = new();
    private readonly DesktopToolExecutor _sut;

    public DesktopToolExecutorTests()
    {
        _sut = new DesktopToolExecutor(_screenshot, _mouse, _keyboard, _terminal, _window, _uiAutomation, _textRecognition);
    }

    [Fact]
    public void Execute_TakeScreenshot_ReturnsPayload()
    {
        string result = _sut.Execute("take_screenshot", "{}");

        Assert.Contains("Base64:", result);
        Assert.Contains("Window under cursor: App=Microsoft Word, Title=Document1, X=0, Y=0, Width=840, Height=640.", result);
        Assert.Contains("Capture bounds: X=0, Y=0, Width=1920, Height=1080.", result);
        Assert.DoesNotContain("Corner pixels:", result);
        Assert.DoesNotContain("Cursor:", result);
        Assert.DoesNotContain("Mouse detail bounds:", result);
        Assert.DoesNotContain("Mouse detail media type:", result);
        Assert.DoesNotContain("Mouse detail base64:", result);
        Assert.DoesNotContain("Coordinate raster:", result);
        Assert.Contains("Original:", result);
        Assert.Contains("Final:", result);
        Assert.Equal(default, _screenshot.LastOptions);
    }

    [Fact]
    public void Execute_TakeScreenshotActiveWindow_UsesWindowBoundsAndPurpose()
    {
        string result = _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"purpose\":\"verify word content\",\"predicted_tool\":\"click\",\"predicted_action\":\"click the save button\",\"predicted_target_label\":\"Save button\",\"padding\":20,\"intended_element_x\":390,\"intended_element_y\":330,\"intended_element_width\":80,\"intended_element_height\":28,\"intended_element_label\":\"Save button\"}");

        Assert.Equal(new WindowBounds(0, 0, 840, 640), _screenshot.LastOptions.Bounds);
        Assert.Contains("Target: active_window.", result);
        Assert.Contains("Visual style: standard.", result);
        Assert.Contains("Purpose: verify word content.", result);
        Assert.Contains("Predicted next tool: click.", result);
        Assert.Contains("Predicted next action: click the save button.", result);
        Assert.Contains("Predicted target/button: Save button.", result);
        Assert.Contains("Capture bounds: X=0, Y=0, Width=840, Height=640.", result);
        Assert.Contains("Window under cursor: App=Microsoft Word, Title=Document1, X=0, Y=0, Width=840, Height=640.", result);
        Assert.Contains("Likely content area: X=34, Y=90, Width=772, Height=518.", result);
        Assert.Contains("Highlighted AX element: X=390, Y=330, Width=80, Height=28, IntersectsCapture=True, Label=Save button.", result);
        Assert.Contains("AX-derived click target: X=430, Y=344, InsideCapture=True, Label=Save button.", result);
        Assert.Contains("Derived schematic target view included:", result);
        Assert.Contains("Supplemental image [schematic-target] media type: image/", result);
        Assert.Contains("Supplemental image [schematic-target] base64:", result);
        Assert.DoesNotContain("Coordinate raster:", result);
    }

    [Fact]
    public void Execute_TakeScreenshotSchematicTarget_DescribesSchematicView()
    {
        string result = _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"purpose\":\"validate calculator target\",\"visual_style\":\"schematic_target\",\"predicted_tool\":\"click\",\"predicted_action\":\"press calculator key 7\",\"predicted_target_label\":\"calculator_key=7\",\"intended_click_x\":420,\"intended_click_y\":360,\"intended_click_label\":\"calculator key 7\",\"intended_element_x\":390,\"intended_element_y\":330,\"intended_element_width\":80,\"intended_element_height\":48,\"intended_element_label\":\"calculator_key=7\"}");

        Assert.Contains("Visual style: schematic_target.", result);
        Assert.Contains("Schematic target overview:", result);
        Assert.Contains("Predicted target/button: calculator_key=7.", result);
        Assert.Contains("Highlighted AX element: X=390, Y=330, Width=80, Height=48, IntersectsCapture=True, Label=calculator_key=7.", result);
        Assert.Contains("AX-derived click target: X=430, Y=354, InsideCapture=True, Label=calculator_key=7.", result);
    }

    [Fact]
    public void Execute_TakeScreenshotSchematicTargetWithoutTarget_ReturnsError()
    {
        string result = _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"visual_style\":\"schematic_target\"}");

        Assert.Equal("[ERROR] visual_style 'schematic_target' requires intended_click coordinates or an intended_element region.", result);
    }

    [Fact]
    public void Execute_TakeScreenshot_WithAxMarks_ReturnsNumberedMarks()
    {
        string result = _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"mark_source\":\"ax\",\"mark_role\":\"AXButton\",\"mark_title\":\"Save\",\"mark_max_count\":1}");

        Assert.Contains("Numbered marks: [1] Source=ax", result);
        Assert.Contains("Label=AXButton Save", result);
        Assert.DoesNotContain("[2]", result);
    }

    [Fact]
    public void Execute_ReadScreenText_ReturnsOcrResultForActiveWindow()
    {
        string result = _sut.Execute("read_screen_text", "{\"target\":\"active_window\",\"purpose\":\"verify Excel values\"}");

        Assert.Equal(new WindowBounds(0, 4, 832, 632), _screenshot.LastOptions.Bounds);
        Assert.Contains("OCR read completed. Target: active_window.", result);
        Assert.Contains("Purpose: verify Excel values.", result);
        Assert.Contains("Recognized text:", result);
        Assert.Contains("1", result);
        Assert.Contains("Confidence=0.99", result);
        Assert.NotEmpty(_textRecognition.LastImageBytes);
    }

    [Fact]
    public void Execute_ReadScreenText_CropsToRequestedRegion()
    {
        string result = _sut.Execute("read_screen_text", "{\"target\":\"active_window\",\"region_x\":100,\"region_y\":120,\"region_width\":140,\"region_height\":90}");

        Assert.Contains("OCR region: X=100, Y=120, Width=140, Height=90.", result);
        Assert.NotEmpty(_textRecognition.LastImageBytes);
    }

    [Fact]
    public void Execute_MoveMouse_UsesMouseService()
    {
        string result = _sut.Execute("move_mouse", "{\"x\":300,\"y\":400}");

        Assert.Equal((300, 400), _mouse.LastMoveTarget);
        Assert.Equal("Mouse moved to (300, 400) via explicit coordinates", result);
    }

    [Fact]
    public void Execute_Drag_UsesMouseService()
    {
        string result = _sut.Execute("drag", "{\"start_x\":120,\"start_y\":180,\"end_x\":420,\"end_y\":260,\"button\":\"left\"}");

        Assert.Equal((120, 180, 420, 260), _mouse.LastDragTarget);
        Assert.Equal(MouseButton.Left, _mouse.LastDragButton);
        Assert.Equal("Dragged Left button from (120, 180) to (420, 260)", result);
    }

    [Fact]
    public void Execute_GetFrontmostUiElements_ReturnsUiAutomationSummary()
    {
        string result = _sut.Execute("get_frontmost_ui_elements", "{}");

        Assert.Contains("Frontmost app: TestApp", result);
        Assert.Contains("AXWindow | title=Main", result);
    }

    [Fact]
    public void Execute_GetFrontmostUiElements_PreservesCalculatorKeyLabelsInSummary()
    {
        _uiAutomation.FrontmostUiSummary = "Frontmost app: Rechner\nFocused window: Rechner at x=861,y=367,w=230,h=408\nVisible UI elements:\n- AXButton | calculator_key=7 | x=871,y=554,w=48,h=48\n- AXButton | calculator_key=4 | x=871,y=608,w=48,h=48";

        string result = _sut.Execute("get_frontmost_ui_elements", "{}");

        Assert.Contains("calculator_key=7", result);
        Assert.Contains("calculator_key=4", result);
    }

    [Fact]
    public void Execute_GetFrontmostApplication_UsesWindowService()
    {
        string result = _sut.Execute("get_frontmost_application", "{}");

        Assert.Equal("Frontmost application: Microsoft Word", result);
    }

    [Fact]
    public void Execute_ListWindows_ReturnsWindowSummary()
    {
        string result = _sut.Execute("list_windows", "{}");

        Assert.Contains("App=Microsoft Word", result);
        Assert.Contains("Title=Document1", result);
        Assert.Contains("Frontmost=True", result);
    }

    [Fact]
    public void Execute_Click_UsesSelectedButton()
    {
        string result = _sut.Execute("click", "{\"x\":100,\"y\":200,\"button\":\"right\"}");

        Assert.Equal((100, 200), _mouse.LastClickTarget);
        Assert.Equal(MouseButton.Right, _mouse.LastClickButton);
        Assert.Equal("Clicked Right button at (100, 200) via explicit coordinates", result);
    }

    [Fact]
    public void Execute_Click_WithIntendedElement_UsesAxElementCenter()
    {
        string result = _sut.Execute("click", "{\"button\":\"left\",\"intended_element_x\":390,\"intended_element_y\":330,\"intended_element_width\":80,\"intended_element_height\":28,\"intended_element_label\":\"Save button\"}");

        Assert.Equal((430, 344), _mouse.LastClickTarget);
        Assert.Equal(MouseButton.Left, _mouse.LastClickButton);
        Assert.Equal("Clicked Left button at (430, 344) via AX element center (Save button)", result);
    }

    [Fact]
    public void Execute_DoubleClick_UsesMouseService()
    {
        string result = _sut.Execute("double_click", "{\"x\":150,\"y\":250}");

        Assert.Equal((150, 250), _mouse.LastDoubleClickTarget);
        Assert.Equal("Double-clicked at (150, 250) via explicit coordinates", result);
    }

    [Fact]
    public void Execute_DoubleClick_WithIntendedElement_UsesAxElementCenter()
    {
        string result = _sut.Execute("double_click", "{\"intended_element_x\":390,\"intended_element_y\":330,\"intended_element_width\":80,\"intended_element_height\":28,\"intended_element_label\":\"Save button\"}");

        Assert.Equal((430, 344), _mouse.LastDoubleClickTarget);
        Assert.Equal("Double-clicked at (430, 344) via AX element center (Save button)", result);
    }

    [Fact]
    public void Execute_Click_WithMarkId_UsesCenterOfLatestScreenshotMark()
    {
        _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"mark_source\":\"ax\",\"mark_role\":\"AXButton\",\"mark_title\":\"Save\",\"mark_max_count\":1}");

        string result = _sut.Execute("click", "{\"mark_id\":1}");

        Assert.Equal((460, 314), _mouse.LastClickTarget);
        Assert.Equal(MouseButton.Left, _mouse.LastClickButton);
        Assert.Contains("via mark 1 (ax: AXButton Save)", result);
    }

    [Fact]
    public void Execute_ReadScreenText_WithMarkId_UsesMarkedBoundsAsOcrRegion()
    {
        _sut.Execute("take_screenshot", "{\"target\":\"active_window\",\"mark_source\":\"ax\",\"mark_role\":\"AXButton\",\"mark_title\":\"Save\",\"mark_max_count\":1}");

        string result = _sut.Execute("read_screen_text", "{\"target\":\"active_window\",\"mark_id\":1}");

        Assert.Contains("OCR region: X=420, Y=300, Width=80, Height=28.", result);
    }

    [Fact]
    public void Execute_Click_WithUnknownMarkId_ReturnsError()
    {
        string result = _sut.Execute("click", "{\"mark_id\":99}");

        Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
        Assert.Contains("Unknown mark_id 99", result);
    }

    [Fact]
    public void Execute_Scroll_UsesMouseService()
    {
        string result = _sut.Execute("scroll", "{\"delta\":-5}");

        Assert.Equal(-5, _mouse.LastScrollDelta);
        Assert.Equal("Scrolled down by 5", result);
    }

    [Fact]
    public void CountOccurrences_ReturnsExpectedCount()
    {
        int count = DesktopToolExecutor.CountOccurrences("alpha beta alpha gamma alpha", "alpha");

        Assert.Equal(3, count);
    }

    [Fact]
    public void ReplaceFirstOccurrence_ReplacesOnlyFirstMatch()
    {
        string updated = DesktopToolExecutor.ReplaceFirstOccurrence("alpha beta alpha", "alpha", "omega");

        Assert.Equal("omega beta alpha", updated);
    }

    [Fact]
    public void BuildWordCreateDocumentScript_UsesNewDocumentAndArgv()
    {
        string script = DesktopToolExecutor.BuildWordCreateDocumentScript();

        Assert.Contains("on run argv", script);
        Assert.Contains("set docRef to make new document", script);
        Assert.Contains("set content of text object of docRef to requestedText", script);
        Assert.Contains("return name of docRef", script);
    }

    [Fact]
    public void BuildWordSetDocumentTextScript_ResolvesDocumentByNameOrActiveDocument()
    {
        string script = DesktopToolExecutor.BuildWordSetDocumentTextScript();

        Assert.Contains("if requestedDocumentName is \"\" then", script);
        Assert.Contains("set docRef to active document", script);
        Assert.Contains("set matchingDocuments to every document whose name is requestedDocumentName", script);
        Assert.Contains("set content of text object of docRef", script);
    }

    [Fact]
    public void BuildWordGetDocumentTextScript_ReturnsDocumentNameAndContent()
    {
        string script = DesktopToolExecutor.BuildWordGetDocumentTextScript();

        Assert.Contains("return (name of docRef) & linefeed & (content of text object of docRef)", script);
    }

    [Fact]
    public void BuildWordFormatTextScript_FormatsMatchingWords()
    {
        string script = DesktopToolExecutor.BuildWordFormatTextScript();

        Assert.Contains("set matchingWords to words of text object of docRef whose content contains requestedSearchText", script);
        Assert.Contains("set bold of font object of wordRange", script);
        Assert.Contains("set italic of font object of wordRange", script);
        Assert.Contains("set underline of font object of wordRange to underline single", script);
        Assert.Contains("return (name of docRef) & linefeed & formattedCount", script);
    }

    [Fact]
    public void Execute_TypeText_UsesKeyboardService()
    {
        string result = _sut.Execute("type_text", "{\"text\":\"Hello\"}");

        Assert.Equal("Hello", _keyboard.LastTypedText);
        Assert.Equal("Typed 5 character(s)", result);
    }

    [Fact]
    public void Execute_TypeText_NormalizesEscapedNewlinesForLongMultilineText()
    {
        string result = _sut.Execute("type_text", "{\"text\":\"Ein Blatt\\\\nEin Baum\\\\nEin Wind\"}");

        Assert.Equal("Ein Blatt\nEin Baum\nEin Wind", _keyboard.LastTypedText);
        Assert.Equal("Typed 27 character(s)", result);
    }

    [Fact]
    public void Execute_TypeText_PreservesLikelyLiteralEscapesForCodeLikeText()
    {
        string result = _sut.Execute("type_text", "{\"text\":\"Console.WriteLine(\\\"a\\\\nb\\\");\"}");

        Assert.Equal("Console.WriteLine(\"a\\nb\");", _keyboard.LastTypedText);
        Assert.Equal("Typed 26 character(s)", result);
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("right right right")]
    [InlineData("pageDown")]
    [InlineData("ENTER")]
    [InlineData("TAB")]
    [InlineData("ESC")]
    [InlineData("BACKSPACE")]
    [InlineData("DOWN")]
    [InlineData("DOWN DOWN DOWN")]
    [InlineData("DOWN,")]
    [InlineData("page down")]
    [InlineData("page down page down")]
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
        Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
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

        Assert.Equal($"{DesktopToolExecutor.ErrorPrefix}Invalid application name: 'bad;app'", result);
    }

    [Fact]
    public void Execute_FocusApplication_RejectsUnsafeName()
    {
        string result = _sut.Execute("focus_application", "{\"name\":\"bad|app\"}");

        Assert.Equal($"{DesktopToolExecutor.ErrorPrefix}Invalid application name: 'bad|app'", result);
    }

    [Fact]
    public void Execute_OpenUrl_RejectsInvalidScheme()
    {
        string result = _sut.Execute("open_url", "{\"url\":\"file:///tmp/test\"}");

        Assert.Equal($"{DesktopToolExecutor.ErrorPrefix}Invalid URL: 'file:///tmp/test'", result);
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

        Assert.Equal($"{DesktopToolExecutor.ErrorPrefix}At least one Apple menu item title is required", result);
    }

    [Fact]
    public void Execute_ClickSystemSettingsSidebarItem_ForwardsTitles()
    {
        string result = _sut.Execute("click_system_settings_sidebar_item", "{\"title\":\"Displays\"}");

        Assert.Equal(new[] { "Displays" }, _uiAutomation.LastSidebarTitles);
        Assert.Contains("Displays", result);
    }

    [Fact]
    public void Execute_FocusWindow_ForwardsFilters()
    {
        string result = _sut.Execute("focus_window", "{\"application_name\":\"Microsoft Word\",\"title_contains\":\"Document1\"}");

        Assert.Equal("Microsoft Word", _window.LastFocusApplicationName);
        Assert.Equal("Document1", _window.LastFocusTitleSubstring);
        Assert.Contains("Focused window matching", result);
    }

    [Fact]
    public void ApplicationNamesMatch_TreatsMicrosoftPrefixAsEquivalent()
    {
        Assert.True(DesktopToolExecutor.ApplicationNamesMatch("Microsoft Word", "Word"));
        Assert.True(DesktopToolExecutor.ApplicationNamesMatch("Word", "Microsoft Word"));
        Assert.False(DesktopToolExecutor.ApplicationNamesMatch("Safari", "Microsoft Word"));
    }

    [Fact]
    public void Execute_FocusApplication_WhenUnsupportedPlatform_ReturnsImplementationError()
    {
        if (OperatingSystem.IsMacOS())
            return;

        string result = _sut.Execute("focus_application", "{\"name\":\"Word\"}");

        Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
        Assert.Contains("not implemented", result);
    }

    [Fact]
    public void Execute_WaitForWindow_ReturnsImmediateMatch()
    {
        string result = _sut.Execute("wait_for_window", "{\"application_name\":\"Microsoft Word\",\"frontmost\":true,\"timeout_ms\":100,\"poll_interval_ms\":50}");

        Assert.Contains("Matched window became available", result);
    }

    [Fact]
    public void Execute_WaitForWindow_UsesFrontmostFallbackWhenAccessibilityEnumerationFails()
    {
        _window.ListWindowsExceptionMessage = "osascript hat keine Berechtigung fuer den Hilfszugriff. (-25211)";

        string result = _sut.Execute("wait_for_window", "{\"application_name\":\"Word\",\"frontmost\":true,\"timeout_ms\":100,\"poll_interval_ms\":50}");

        if (!OperatingSystem.IsMacOS())
        {
            Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
            return;
        }

        Assert.Contains("Matched window became available", result);
        Assert.Contains("Fallback used frontmost application Microsoft Word", result);
    }

    [Fact]
    public void Execute_ListWindows_UsesFrontmostFallbackMessageWhenAccessibilityEnumerationFails()
    {
        _window.ListWindowsExceptionMessage = "System Events got an error: osascript hat keine Berechtigung fuer den Hilfszugriff. (-25211)";

        string result = _sut.Execute("list_windows", "{}");

        if (!OperatingSystem.IsMacOS())
        {
            Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
            return;
        }

        Assert.Contains("Window listing unavailable because macOS Accessibility permission is missing", result);
        Assert.Contains("Frontmost application: Microsoft Word", result);
    }

    [Fact]
    public void Execute_FocusFrontmostWindowContent_ForwardsApplicationName()
    {
        string result = _sut.Execute("focus_frontmost_window_content", "{\"application_name\":\"Safari\"}");

        Assert.Equal("Safari", _uiAutomation.LastFocusedContentApplicationName);
        Assert.Equal("Focused frontmost window content for Safari", result);
    }

    [Fact]
    public void Execute_FindUiElement_ForwardsFilters()
    {
        string result = _sut.Execute("find_ui_element", "{\"title\":\"Save\",\"role\":\"AXButton\"}");

        Assert.Equal("Save", _uiAutomation.LastFindTitle);
        Assert.Equal("AXButton", _uiAutomation.LastFindRole);
        Assert.Contains("Role=AXButton", result);
        Assert.Contains("Title=Save", result);
    }

    [Fact]
    public void Execute_ClickUiElement_ForwardsFilters()
    {
        string result = _sut.Execute("click_ui_element", "{\"title\":\"Save\",\"role\":\"AXButton\",\"match_index\":1}");

        Assert.Equal("Save", _uiAutomation.LastClickTitle);
        Assert.Equal("AXButton", _uiAutomation.LastClickRole);
        Assert.Equal(1, _uiAutomation.LastClickMatchIndex);
        Assert.Contains("Clicked UI element", result);
    }

    [Fact]
    public void Execute_WaitForUiElement_ReturnsImmediateMatch()
    {
        string result = _sut.Execute("wait_for_ui_element", "{\"title\":\"Save\",\"role\":\"AXButton\",\"timeout_ms\":100,\"poll_interval_ms\":50}");

        Assert.Contains("Matched UI element became available", result);
    }

    [Fact]
    public void Execute_GetFocusedUiElement_ReturnsFocusedElement()
    {
        string result = _sut.Execute("get_focused_ui_element", "{}");

        Assert.Contains("Focused UI element:", result);
        Assert.Contains("Title=Filename", result);
    }

    [Fact]
    public void Execute_AssertState_FrontmostApplicationPasses()
    {
        string result = _sut.Execute("assert_state", "{\"state\":\"frontmost_application\",\"application_name\":\"Word\"}");

        Assert.Contains("Assertion passed", result);
        Assert.Contains("Frontmost application is Microsoft Word", result);
    }

    [Fact]
    public void Execute_AssertState_FocusedUiElementPasses()
    {
        string result = _sut.Execute("assert_state", "{\"state\":\"focused_ui_element\",\"title_contains\":\"Filename\",\"role\":\"AXTextField\"}");

        Assert.Contains("Assertion passed", result);
        Assert.Contains("Focused UI element", result);
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

        Assert.Equal($"{DesktopToolExecutor.ErrorPrefix}Unknown tool: does_not_exist", result);
    }

    [Fact]
    public void Execute_TypeText_BlockedNavigationLikeInput_IsMarkedAsError()
    {
        string result = _sut.Execute("type_text", "{\"text\":\"ENTER\"}");

        Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
    }

    [Fact]
    public void Execute_InvalidJson_ReturnsPrefixedErrorInsteadOfThrowing()
    {
        string result = _sut.Execute("move_mouse", "{not-json}");

        Assert.StartsWith(DesktopToolExecutor.ErrorPrefix, result);
        Assert.Contains("Tool 'move_mouse' failed", result);
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
