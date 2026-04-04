using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Tests;

public sealed class DesktopToolDefinitionsTests
{
    [Fact]
    public void All_ContainsExpectedTools()
    {
        var names = DesktopToolDefinitions.GetChatTools().Select(t => t.FunctionName).ToList();

        Assert.Contains("take_screenshot",    names);
        Assert.Contains("get_screen_info",    names);
        Assert.Contains("get_cursor_position",names);
        Assert.Contains("move_mouse",         names);
        Assert.Contains("click",              names);
        Assert.Contains("double_click",       names);
        Assert.Contains("scroll",             names);
        Assert.Contains("type_text",          names);
        Assert.Contains("press_key",          names);
        Assert.Contains("open_application",   names);
        Assert.Contains("focus_application",  names);
        Assert.Contains("open_url",           names);
        Assert.Contains("run_command",        names);
        Assert.Contains("click_dock_application", names);
        Assert.Contains("click_apple_menu_item", names);
        Assert.Contains("click_system_settings_sidebar_item", names);
        Assert.Contains("focus_frontmost_window_content", names);
        Assert.Contains("get_active_window_bounds", names);
        Assert.Contains("move_active_window", names);
        Assert.Contains("resize_active_window", names);
        Assert.Contains("wait",               names);
    }

    [Fact]
    public void All_HasAtLeastTenTools()
    {
        Assert.True(DesktopToolDefinitions.GetChatTools().Count >= 10,
            $"Expected at least 10 tools but found {DesktopToolDefinitions.GetChatTools().Count}");
    }

    [Theory]
    [InlineData("""{"x":100,"y":200}""", "x", 100)]
    [InlineData("""{"x":0,"y":0}""",     "y", 0)]
    [InlineData("""{"delta":-3}""",      "delta", -3)]
    public void ParseArgs_And_GetInt_ReturnsExpectedValue(string json, string key, int expected)
    {
        var args   = DesktopToolDefinitions.ParseArgs(json);
        int actual = DesktopToolDefinitions.GetInt(args, key);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("""{"button":"right"}""", "button", "right")]
    [InlineData("""{"name":"Safari"}""",  "name",   "Safari")]
    [InlineData("""{"text":"hello"}""",  "text",   "hello")]
    public void ParseArgs_And_GetString_ReturnsExpectedValue(string json, string key, string expected)
    {
        var    args   = DesktopToolDefinitions.ParseArgs(json);
        string actual = DesktopToolDefinitions.GetString(args, key);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseArgs_EmptyJson_ReturnsEmptyDictionary()
    {
        var args = DesktopToolDefinitions.ParseArgs(string.Empty);
        Assert.Empty(args);
    }

    [Fact]
    public void ParseArgs_NullJson_ReturnsEmptyDictionary()
    {
        var args = DesktopToolDefinitions.ParseArgs(null!);
        Assert.Empty(args);
    }

    [Fact]
    public void GetInt_MissingKey_ReturnsDefault()
    {
        var args = DesktopToolDefinitions.ParseArgs("{}");
        Assert.Equal(42, DesktopToolDefinitions.GetInt(args, "missing", 42));
    }

    [Fact]
    public void GetString_MissingKey_ReturnsDefault()
    {
        var args = DesktopToolDefinitions.ParseArgs("{}");
        Assert.Equal("default", DesktopToolDefinitions.GetString(args, "missing", "default"));
    }

    [Fact]
    public void GetStringArray_ReturnsExpectedValues()
    {
        var args = DesktopToolDefinitions.ParseArgs("""{"arguments":["status","--short"]}""");

        IReadOnlyList<string> values = DesktopToolDefinitions.GetStringArray(args, "arguments");

        Assert.Equal(["status", "--short"], values);
    }

    [Fact]
    public void GetBool_ReturnsExpectedValue()
    {
        var args = DesktopToolDefinitions.ParseArgs("""{"double_click":true}""");

        bool value = DesktopToolDefinitions.GetBool(args, "double_click");

        Assert.True(value);
    }

    [Fact]
    public void TakeScreenshotDefinition_DescribesFocusedCaptureAndPurpose()
    {
        DesktopFunctionToolDefinition definition = DesktopToolDefinitions.FunctionDefinitions.Single(x => x.Name == "take_screenshot");
        string parameters = definition.Parameters?.ToString() ?? string.Empty;

        Assert.Contains("active_window", definition.Description);
        Assert.Contains("purpose", definition.Description);
        Assert.Contains("purpose", parameters);
        Assert.Contains("padding", parameters);
        Assert.Contains("intended_click_x", parameters);
        Assert.Contains("intended_click_y", parameters);
        Assert.Contains("intended_click_label", parameters);
    }
}
