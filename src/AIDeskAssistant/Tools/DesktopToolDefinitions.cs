using System.Text.Json;
using OpenAI.Chat;

namespace AIDeskAssistant.Tools;

/// <summary>Provides the ChatTool definitions exposed to the OpenAI model.</summary>
internal static class DesktopToolDefinitions
{
    public static IReadOnlyList<ChatTool> All { get; } = new List<ChatTool>
    {
        ChatTool.CreateFunctionTool(
            "take_screenshot",
            "Takes a full-screen screenshot. Returns the image encoded as a base64 PNG string so the AI can analyse the screen contents."
        ),

        ChatTool.CreateFunctionTool(
            "get_screen_info",
            "Returns information about the primary monitor: width, height, and bit depth."
        ),

        ChatTool.CreateFunctionTool(
            "get_cursor_position",
            "Returns the current X/Y position of the mouse cursor in screen coordinates."
        ),

        ChatTool.CreateFunctionTool(
            "move_mouse",
            "Moves the mouse cursor to the specified absolute screen coordinates.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "X coordinate (pixels from left edge)" },
                "y": { "type": "integer", "description": "Y coordinate (pixels from top edge)" }
              },
              "required": ["x", "y"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "click",
            "Moves the mouse cursor to (x, y) and clicks the specified mouse button.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "X coordinate" },
                "y": { "type": "integer", "description": "Y coordinate" },
                "button": {
                  "type": "string",
                  "enum": ["left", "right", "middle"],
                  "description": "Mouse button to click (default: left)"
                }
              },
              "required": ["x", "y"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "double_click",
            "Moves the mouse cursor to (x, y) and double-clicks the left mouse button.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "X coordinate" },
                "y": { "type": "integer", "description": "Y coordinate" }
              },
              "required": ["x", "y"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "scroll",
            "Scrolls the mouse wheel at the current cursor position. Positive delta scrolls up, negative scrolls down.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "delta": {
                  "type": "integer",
                  "description": "Scroll amount: positive = scroll up/forward, negative = scroll down/backward"
                }
              },
              "required": ["delta"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "type_text",
            "Types the specified text string using the keyboard.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "text": { "type": "string", "description": "The text to type" }
              },
              "required": ["text"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "press_key",
            "Presses a keyboard key or key combination such as 'enter', 'ctrl+c', 'alt+F4', 'cmd+space'.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "key": {
                  "type": "string",
                  "description": "Key or combination using '+' as separator, e.g. 'enter', 'ctrl+c', 'cmd+space'"
                }
              },
              "required": ["key"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "open_application",
            "Opens an application by its name using the operating system's default launcher.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "name": {
                  "type": "string",
                  "description": "Application name, e.g. 'Safari', 'Chrome', 'Notepad', 'Terminal'"
                }
              },
              "required": ["name"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "open_url",
            "Opens the specified http/https URL in the user's default browser. Use this for web tasks such as opening Gmail, navigating to shops, or continuing longer browser workflows.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "url": {
                  "type": "string",
                  "description": "An absolute http or https URL, e.g. 'https://mail.google.com/'"
                }
              },
              "required": ["url"]
            }
            """)
        ),

        ChatTool.CreateFunctionTool(
            "wait",
            "Waits for the specified number of milliseconds before continuing.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "milliseconds": {
                  "type": "integer",
                  "description": "Number of milliseconds to wait (min 100, max 10000)"
                }
              },
              "required": ["milliseconds"]
            }
            """)
        ),
    };

    /// <summary>Parses a JSON arguments string into a dictionary for easy access.</summary>
    public static Dictionary<string, JsonElement> ParseArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, JsonElement>();
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
               ?? new Dictionary<string, JsonElement>();
    }

    /// <summary>Gets a string value from the args dictionary, returning a default if missing.</summary>
    public static string GetString(Dictionary<string, JsonElement> args, string key, string defaultValue = "")
        => args.TryGetValue(key, out var v) ? v.GetString() ?? defaultValue : defaultValue;

    /// <summary>Gets an integer value from the args dictionary, returning a default if missing.</summary>
    public static int GetInt(Dictionary<string, JsonElement> args, string key, int defaultValue = 0)
        => args.TryGetValue(key, out var v) ? v.GetInt32() : defaultValue;
}
