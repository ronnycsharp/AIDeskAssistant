using System.Text.Json;
using OpenAI.Chat;

namespace AIDeskAssistant.Tools;

internal sealed record DesktopFunctionToolDefinition(string Name, string Description, BinaryData? Parameters = null);

/// <summary>Provides the ChatTool definitions exposed to the OpenAI model.</summary>
internal static class DesktopToolDefinitions
{
  public static IReadOnlyList<DesktopFunctionToolDefinition> FunctionDefinitions { get; } = new List<DesktopFunctionToolDefinition>
    {
    new(
            "take_screenshot",
          "Takes a screenshot for visual verification. Prefer target='active_window' for app-specific tasks like Word, Mail, or browsers to reduce payload size and keep only the relevant UI. Include a short purpose so the model can understand what this screenshot is meant to validate. Before a state-changing action, also include the predicted next tool and predicted target/button label so the screenshot result clearly states which action is being prepared. Use visual_style='standard' for the untouched original crop; when you also provide an intended target, the tool returns an additional derived schematic target view generated from that same screenshot so the intended button or text area is emphasized before the click or keyboard step. Use visual_style='schematic_target' only when you explicitly want the processed target-focused rendering as the primary image. If you identified an AX/UI element by its frame, provide that frame so the screenshot can highlight the button and derive the click point from the center of the AX rectangle.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "target": {
                  "type": "string",
                  "enum": ["full_screen", "active_window"],
                  "description": "Capture the full screen or only the active window. Prefer 'active_window' when the task is confined to one app window."
                },
                "purpose": {
                  "type": "string",
                  "description": "Short reason for the screenshot, e.g. 'verify poem text is visible in the Word document body'."
                },
                "visual_style": {
                  "type": "string",
                  "enum": ["standard", "schematic_target"],
                  "description": "Optional rendering style for the screenshot. Use 'standard' for the untouched original crop; if you also provide intended target metadata, the result additionally includes a derived schematic target image from the same capture. Use 'schematic_target' only when you want the processed target-focused rendering as the primary image."
                },
                "predicted_tool": {
                  "type": "string",
                  "description": "Optional exact next tool you expect to call after reviewing this screenshot, e.g. 'click', 'double_click', 'press_key', 'click_ui_element', or 'run_command'."
                },
                "predicted_action": {
                  "type": "string",
                  "description": "Optional short description of the next intended action, e.g. 'click the Save button' or 'press Enter in the focused dialog'."
                },
                "predicted_target_label": {
                  "type": "string",
                  "description": "Optional short label for the predicted UI target or button, e.g. 'Save button', 'Word document body', or 'Run button'."
                },
                "padding": {
                  "type": "integer",
                  "description": "Optional extra pixels around the active window capture (0-200). Ignored for full_screen."
                },
                "intended_click_x": {
                  "type": "integer",
                  "description": "Optional X coordinate of the click you are about to perform. When an AX/UI element frame is also provided, the center of that frame should be preferred as the click position."
                },
                "intended_click_y": {
                  "type": "integer",
                  "description": "Optional Y coordinate of the click you are about to perform. When an AX/UI element frame is also provided, the center of that frame should be preferred as the click position."
                },
                "intended_click_label": {
                  "type": "string",
                  "description": "Optional short label for the intended click target, e.g. 'Word document body' or 'Save button'."
                },
                "intended_element_x": {
                  "type": "integer",
                  "description": "Optional X coordinate of an AX/UI element frame you want highlighted in the screenshot. Use together with intended_element_y, intended_element_width, and intended_element_height. The click point should be derived from the center of this frame."
                },
                "intended_element_y": {
                  "type": "integer",
                  "description": "Optional Y coordinate of an AX/UI element frame you want highlighted in the screenshot."
                },
                "intended_element_width": {
                  "type": "integer",
                  "description": "Optional width of the AX/UI element frame to highlight in the screenshot."
                },
                "intended_element_height": {
                  "type": "integer",
                  "description": "Optional height of the AX/UI element frame to highlight in the screenshot."
                },
                "intended_element_label": {
                  "type": "string",
                  "description": "Optional short label for the highlighted AX/UI element, e.g. 'Save button' or 'Word toolbar button'."
                },
                "mark_source": {
                  "type": "string",
                  "enum": ["none", "ax", "ocr", "ax_and_ocr"],
                  "description": "Optional numbered mark overlay mode. 'ax' marks Accessibility UI elements, 'ocr' marks OCR text boxes, and 'ax_and_ocr' combines both."
                },
                "mark_max_count": {
                  "type": "integer",
                  "description": "Maximum number of numbered marks to render and expose for follow-up actions (1-40)."
                },
                "mark_title": {
                  "type": "string",
                  "description": "Optional AX title filter used when mark_source includes 'ax'."
                },
                "mark_role": {
                  "type": "string",
                  "description": "Optional AX role filter used when mark_source includes 'ax'."
                },
                "mark_value": {
                  "type": "string",
                  "description": "Optional AX value filter used when mark_source includes 'ax'."
                },
                "mark_text_contains": {
                  "type": "string",
                  "description": "Optional substring filter used when mark_source includes 'ocr'."
                }
              }
            }
            """)
        ),

    new(
            "get_screen_info",
            "Returns information about the primary monitor: width, height, and bit depth."
        ),

    new(
            "read_screen_text",
            "Uses native Apple Vision OCR on macOS to read visible text from the full screen, active window, or a specific screen region. Prefer this for verification of spreadsheet values, dialog text, filenames, and other text-heavy UI state after actions. Use keyboard-first workflows whenever possible, then verify the result with OCR instead of guessing from pixels alone.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "target": {
                  "type": "string",
                  "enum": ["full_screen", "active_window"],
                  "description": "Capture the full screen or only the active window before running OCR. Prefer 'active_window' for app-specific verification."
                },
                "purpose": {
                  "type": "string",
                  "description": "Short reason for the OCR read, e.g. 'verify Excel values in column A'."
                },
                "padding": {
                  "type": "integer",
                  "description": "Optional extra pixels around the active window capture (0-200). Ignored for full_screen."
                },
                "region_x": {
                  "type": "integer",
                  "description": "Optional screen-space X coordinate of a smaller region to OCR. Use together with region_y, region_width, and region_height."
                },
                "region_y": {
                  "type": "integer",
                  "description": "Optional screen-space Y coordinate of a smaller region to OCR."
                },
                "region_width": {
                  "type": "integer",
                  "description": "Optional width of the OCR region in pixels."
                },
                "region_height": {
                  "type": "integer",
                  "description": "Optional height of the OCR region in pixels."
                },
                "mark_id": {
                  "type": "integer",
                  "description": "Optional numbered mark from the latest marked screenshot. When provided, OCR reads exactly that mark region."
                }
              }
            }
            """)
        ),

    new(
            "find_text_on_screen",
            "Uses native Apple Vision OCR on macOS to find visible text on the full screen, active window, or a specific region and returns clickable center coordinates for each match. Prefer this when you need to target visible labels, buttons, links, or sheet references by text instead of guessing coordinates.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "text": {
                  "type": "string",
                  "description": "Case-insensitive text fragment to search for in visible OCR results."
                },
                "target": {
                  "type": "string",
                  "enum": ["full_screen", "active_window"],
                  "description": "Capture the full screen or only the active window before searching OCR text. Prefer 'active_window' for app-specific targeting."
                },
                "purpose": {
                  "type": "string",
                  "description": "Short reason for the lookup, e.g. 'find the Save button label' or 'locate A1 in Excel'."
                },
                "padding": {
                  "type": "integer",
                  "description": "Optional extra pixels around the active window capture (0-200). Ignored for full_screen."
                },
                "region_x": {
                  "type": "integer",
                  "description": "Optional screen-space X coordinate of a smaller region to search. Use together with region_y, region_width, and region_height."
                },
                "region_y": {
                  "type": "integer",
                  "description": "Optional screen-space Y coordinate of a smaller region to search."
                },
                "region_width": {
                  "type": "integer",
                  "description": "Optional width of the search region in pixels."
                },
                "region_height": {
                  "type": "integer",
                  "description": "Optional height of the search region in pixels."
                },
                "mark_id": {
                  "type": "integer",
                  "description": "Optional numbered mark from the latest marked screenshot. When provided, the text search is limited to that mark region."
                },
                "max_results": {
                  "type": "integer",
                  "description": "Optional maximum number of matches to return (1-40)."
                }
              },
              "required": ["text"]
            }
            """)
        ),

    new(
            "get_frontmost_ui_elements",
            "Returns a compact Accessibility-based summary of the currently frontmost macOS application and its visible UI elements. Use this to understand app structure before clicking or typing."
        ),

    new(
            "get_frontmost_application",
            "Returns the display name of the currently frontmost desktop application. Use this to verify focus before typing or clicking.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {}
            }
            """)
        ),

    new(
            "list_windows",
            "Lists visible top-level windows with app name, title, bounds, and frontmost/minimized state. Use this to reason about which window should be focused or whether dialogs are still present.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {}
            }
            """)
        ),

    new(
            "focus_window",
            "Focuses a matching window by application name and/or title substring. Prefer this when a specific document or dialog must be brought to the foreground, not just the app in general.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "application_name": {
                  "type": "string",
                  "description": "Optional application name filter, e.g. 'Microsoft Word'."
                },
                "title_contains": {
                  "type": "string",
                  "description": "Optional window title substring, e.g. 'Document2' or 'Save'."
                }
              }
            }
            """)
        ),

    new(
            "wait_for_window",
            "Polls until a matching window appears, disappears, or becomes frontmost. Use this instead of blind waits when opening dialogs, waiting for documents, or checking that a modal closed.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "application_name": {
                  "type": "string",
                  "description": "Optional application name filter."
                },
                "title_contains": {
                  "type": "string",
                  "description": "Optional window title substring filter."
                },
                "frontmost": {
                  "type": "boolean",
                  "description": "When true, require the window to be the frontmost one."
                },
                "absent": {
                  "type": "boolean",
                  "description": "When true, wait until no matching window remains."
                },
                "timeout_ms": {
                  "type": "integer",
                  "description": "Maximum wait time in milliseconds."
                },
                "poll_interval_ms": {
                  "type": "integer",
                  "description": "Polling interval in milliseconds."
                }
              }
            }
            """)
        ),

    new(
            "get_cursor_position",
            "Returns the current X/Y position of the mouse cursor in screen coordinates."
        ),

    new(
            "move_mouse",
            "Moves the mouse cursor to the specified absolute screen coordinates, to the center of a numbered mark from the latest marked screenshot, or to the automatically derived center of a provided AX/UI element frame.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "X coordinate (pixels from left edge). Optional when you provide a mark_id or AX element frame." },
                "y": { "type": "integer", "description": "Y coordinate (pixels from top edge). Optional when you provide a mark_id or AX element frame." },
                "intended_element_x": { "type": "integer", "description": "Optional X coordinate of an AX/UI element frame. When the full frame is provided, the pointer target is derived from the center of that rectangle." },
                "intended_element_y": { "type": "integer", "description": "Optional Y coordinate of an AX/UI element frame." },
                "intended_element_width": { "type": "integer", "description": "Optional width of the AX/UI element frame." },
                "intended_element_height": { "type": "integer", "description": "Optional height of the AX/UI element frame." },
                "intended_element_label": { "type": "string", "description": "Optional label for the AX/UI element whose center is being targeted." },
                "mark_id": { "type": "integer", "description": "Optional numbered mark from the latest marked screenshot. When provided, the cursor moves to the center of that mark." }
              }
            }
            """)
        ),

          new(
            "drag",
            "Performs a drag gesture from one absolute screen coordinate to another using the specified mouse button. Use this for selecting text, moving windows, resizing panes, or dragging sliders and files.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "start_x": { "type": "integer", "description": "Starting X coordinate" },
                "start_y": { "type": "integer", "description": "Starting Y coordinate" },
                "end_x": { "type": "integer", "description": "Ending X coordinate" },
                "end_y": { "type": "integer", "description": "Ending Y coordinate" },
                "button": {
                  "type": "string",
                  "enum": ["left", "right", "middle"],
                  "description": "Mouse button to hold during the drag (default: left)"
                }
              },
              "required": ["start_x", "start_y", "end_x", "end_y"]
            }
            """)
        ),

          new(
            "click",
            "Moves the mouse cursor to (x, y), to the center of a numbered mark, or to the automatically derived center of a provided AX/UI element frame, then clicks the specified mouse button.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "X coordinate. Optional when you provide a mark_id or AX element frame." },
                "y": { "type": "integer", "description": "Y coordinate. Optional when you provide a mark_id or AX element frame." },
                "intended_element_x": { "type": "integer", "description": "Optional X coordinate of an AX/UI element frame. When the full frame is provided, the click target is derived from the center of that rectangle." },
                "intended_element_y": { "type": "integer", "description": "Optional Y coordinate of an AX/UI element frame." },
                "intended_element_width": { "type": "integer", "description": "Optional width of the AX/UI element frame." },
                "intended_element_height": { "type": "integer", "description": "Optional height of the AX/UI element frame." },
                "intended_element_label": { "type": "string", "description": "Optional label for the AX/UI element whose center is being clicked." },
                "mark_id": { "type": "integer", "description": "Optional numbered mark from the latest marked screenshot. When provided, the click targets the center of that mark." },
                "button": {
                  "type": "string",
                  "enum": ["left", "right", "middle"],
                  "description": "Mouse button to click (default: left)"
                }
              }
            }
            """)
        ),

          new(
            "double_click",
            "Moves the mouse cursor to (x, y), to the center of a numbered mark, or to the automatically derived center of a provided AX/UI element frame, then double-clicks the left mouse button.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "X coordinate. Optional when you provide a mark_id or AX element frame." },
                "y": { "type": "integer", "description": "Y coordinate. Optional when you provide a mark_id or AX element frame." },
                "intended_element_x": { "type": "integer", "description": "Optional X coordinate of an AX/UI element frame. When the full frame is provided, the double-click target is derived from the center of that rectangle." },
                "intended_element_y": { "type": "integer", "description": "Optional Y coordinate of an AX/UI element frame." },
                "intended_element_width": { "type": "integer", "description": "Optional width of the AX/UI element frame." },
                "intended_element_height": { "type": "integer", "description": "Optional height of the AX/UI element frame." },
                "intended_element_label": { "type": "string", "description": "Optional label for the AX/UI element whose center is being double-clicked." },
                "mark_id": { "type": "integer", "description": "Optional numbered mark from the latest marked screenshot. When provided, the double-click targets the center of that mark." }
              }
            }
            """)
        ),

          new(
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

          new(
            "type_text",
            "Types the specified literal text string using the keyboard. Use this only for document or form content. Do not encode special keys like enter, return, tab, escape, or arrow keys as words inside the text; use press_key for those.",
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

          new(
            "word_create_document",
            "On macOS, creates a new Microsoft Word document through Word's native AppleScript interface. Prefer this for plain-text Word document creation instead of fragile UI navigation through the start screen.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "text": {
                  "type": "string",
                  "description": "Optional initial plain-text document content to write immediately after creating the document."
                },
                "activate": {
                  "type": "boolean",
                  "description": "When true, activates Microsoft Word before creating the document. Defaults to true."
                }
              }
            }
            """)
        ),

          new(
            "word_set_document_text",
            "On macOS, sets or appends plain text in a Microsoft Word document through Word's native AppleScript interface. Prefer this for plain-text document content updates when formatting preservation is not required.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "text": {
                  "type": "string",
                  "description": "Plain text to write into the target Word document."
                },
                "document_name": {
                  "type": "string",
                  "description": "Optional Word document name. When omitted, the active document is used."
                },
                "append": {
                  "type": "boolean",
                  "description": "When true, appends the text to the current document content instead of replacing it."
                },
                "activate": {
                  "type": "boolean",
                  "description": "When true, activates Microsoft Word before writing. Defaults to true."
                }
              },
              "required": ["text"]
            }
            """)
        ),

          new(
            "word_replace_text",
            "On macOS, replaces plain text inside a Microsoft Word document through Word's native AppleScript interface. Prefer this over search-panel UI automation for straightforward Word text replacement tasks.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "search_text": {
                  "type": "string",
                  "description": "Plain text to search for in the target Word document."
                },
                "replacement_text": {
                  "type": "string",
                  "description": "Plain text that replaces the matched search text."
                },
                "document_name": {
                  "type": "string",
                  "description": "Optional Word document name. When omitted, the active document is used."
                },
                "replace_all": {
                  "type": "boolean",
                  "description": "When true, replace every occurrence. Otherwise only the first occurrence is replaced."
                },
                "activate": {
                  "type": "boolean",
                  "description": "When true, activates Microsoft Word before replacing. Defaults to true."
                }
              },
              "required": ["search_text", "replacement_text"]
            }
            """)
        ),

          new(
            "word_format_text",
            "On macOS, applies simple formatting like bold, italic, or underline to matching Microsoft Word words through Word's native AppleScript interface. Prefer this for straightforward word-level formatting instead of fragile selection-based UI automation.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "search_text": {
                  "type": "string",
                  "description": "Word text to match for formatting. This tool is best for straightforward word-level matches."
                },
                "document_name": {
                  "type": "string",
                  "description": "Optional Word document name. When omitted, the active document is used."
                },
                "bold": {
                  "type": "boolean",
                  "description": "Optional bold state to apply to matching words."
                },
                "italic": {
                  "type": "boolean",
                  "description": "Optional italic state to apply to matching words."
                },
                "underline": {
                  "type": "boolean",
                  "description": "Optional underline state to apply to matching words."
                },
                "activate": {
                  "type": "boolean",
                  "description": "When true, activates Microsoft Word before formatting. Defaults to true."
                }
              },
              "required": ["search_text"]
            }
            """)
        ),

          new(
            "press_key",
            "Presses a keyboard key or key combination such as 'enter', 'ctrl+c', 'alt+F4', 'cmd+space'. Use this for enter, return, tab, escape, arrows, delete, and shortcuts instead of typing those words with type_text.",
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

          new(
            "open_application",
            "Opens an application by its name using the operating system's default launcher and should bring it to the foreground. On macOS, app aliases such as 'Word' may resolve to the native app name like 'Microsoft Word'. After calling this, confirm the app is actually frontmost before typing.",
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

          new(
            "focus_application",
            "Brings an already running desktop application to the foreground without launching a new instance. Use this before typing or pressing keys into apps like Microsoft Word, Safari, Mail, Calendar, or Blender if another app may have stolen focus.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "name": {
                  "type": "string",
                  "description": "Application name, e.g. 'Microsoft Word', 'Safari', 'Blender'"
                }
              },
              "required": ["name"]
            }
            """)
        ),

          new(
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

          new(
            "run_command",
            "Runs an installed CLI executable and returns its stdout/stderr text output. Use this for terminal tasks when reading command output is more reliable than screenshots. The command is run directly without a shell, so shell syntax like pipes, redirection, globbing, ~, $HOME, or $(whoami) will not be expanded.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "command": {
                  "type": "string",
                  "description": "Executable name only, e.g. 'git', 'dotnet', 'python3', 'npm'"
                },
                "arguments": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Command arguments as separate strings, e.g. ['status', '--short']"
                },
                "timeout_ms": {
                  "type": "integer",
                  "description": "How long to wait before timing out (min 100, max 60000)"
                }
              },
              "required": ["command"]
            }
            """)
        ),

          new(
            "click_dock_application",
            "On macOS, clicks an application icon in the Dock by title using Accessibility APIs. Prefer this when you want to start or foreground a GUI app the same way a human user would, for example Mail, Calendar, Microsoft Word, Safari, or Blender.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Preferred Dock app title to match, e.g. 'Microsoft Word' or 'Blender'"
                },
                "alternate_titles": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional alternate titles to try, e.g. ['Word']"
                }
              },
              "required": ["title"]
            }
            """)
        ),

          new(
            "click_apple_menu_item",
            "On macOS, opens the Apple menu and clicks the matching menu item by title using Accessibility APIs. Prefer this over screenshot-based clicking for native Apple menu items such as System Settings or About This Mac.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Preferred title to match, e.g. 'System Settings'"
                },
                "alternate_titles": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional alternate localized titles to try, e.g. ['System Preferences']"
                }
              },
              "required": ["title"]
            }
            """)
        ),

          new(
            "click_system_settings_sidebar_item",
            "On macOS, clicks a sidebar item in System Settings by title using Accessibility APIs. Prefer this over screenshot-based clicking for native sidebar navigation such as Wi-Fi, Bluetooth, or General.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Preferred sidebar title to match, e.g. 'Wi-Fi'"
                },
                "alternate_titles": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional alternate localized titles to try, e.g. ['WLAN']"
                }
              },
              "required": ["title"]
            }
            """)
        ),

          new(
            "focus_frontmost_window_content",
            "On macOS, focuses a likely content or editable region inside the current frontmost window using Accessibility APIs. Prefer this before typing into Word documents, editors, mail composers, or other desktop document areas so text does not land in a toolbar, ribbon, menu, or search field.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "application_name": {
                  "type": "string",
                  "description": "Optional expected frontmost application name, e.g. 'Microsoft Word'"
                }
              }
            }
            """)
        ),

          new(
            "find_ui_element",
            "Finds matching Accessibility UI elements in the frontmost macOS window by title, role, and/or value. Use this to localize buttons, fields, rows, checkboxes, or other controls before clicking or validating state.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Optional title substring to match."
                },
                "role": {
                  "type": "string",
                  "description": "Optional Accessibility role filter, e.g. 'AXButton' or 'AXTextField'."
                },
                "value": {
                  "type": "string",
                  "description": "Optional value substring to match."
                }
              }
            }
            """)
        ),

          new(
            "click_ui_element",
            "Clicks a matching Accessibility UI element in the frontmost macOS window. Prefer this over coordinate clicking when a button, checkbox, row, or menu-like control can be identified structurally.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Optional title substring to match."
                },
                "role": {
                  "type": "string",
                  "description": "Optional Accessibility role filter."
                },
                "value": {
                  "type": "string",
                  "description": "Optional value substring to match."
                },
                "match_index": {
                  "type": "integer",
                  "description": "Zero-based match index to click when multiple elements match."
                }
              }
            }
            """)
        ),

          new(
            "wait_for_ui_element",
            "Polls until a matching Accessibility UI element appears or disappears in the frontmost macOS window. Use this instead of blind waits for buttons, dialogs, rows, fields, or save confirmations.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Optional title substring to match."
                },
                "role": {
                  "type": "string",
                  "description": "Optional Accessibility role filter."
                },
                "value": {
                  "type": "string",
                  "description": "Optional value substring to match."
                },
                "absent": {
                  "type": "boolean",
                  "description": "When true, wait until the matching UI element is no longer present."
                },
                "timeout_ms": {
                  "type": "integer",
                  "description": "Maximum wait time in milliseconds."
                },
                "poll_interval_ms": {
                  "type": "integer",
                  "description": "Polling interval in milliseconds."
                }
              }
            }
            """)
        ),

          new(
            "get_focused_ui_element",
            "Returns the currently focused Accessibility UI element in the frontmost macOS application/window. Use this to verify where typing or key presses will land.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {}
            }
            """)
        ),

          new(
            "assert_state",
            "Checks a specific desktop state and returns pass/fail with details. Use this to verify frontmost app, window presence, UI element presence, or focused UI element before declaring a task complete.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "state": {
                  "type": "string",
                  "enum": ["frontmost_application", "window_present", "ui_element_present", "focused_ui_element"],
                  "description": "Which state to assert."
                },
                "application_name": {
                  "type": "string",
                  "description": "Expected application name or filter, depending on the state."
                },
                "title_contains": {
                  "type": "string",
                  "description": "Expected window title substring or UI element title substring."
                },
                "role": {
                  "type": "string",
                  "description": "Optional Accessibility role filter for UI element assertions."
                },
                "value": {
                  "type": "string",
                  "description": "Optional UI element value filter."
                },
                "expected": {
                  "type": "boolean",
                  "description": "Expected truth value. Defaults to true."
                }
              },
              "required": ["state"]
            }
            """)
        ),

          new(
            "get_active_window_bounds",
            "Returns the x/y position and width/height of the currently active/focused window.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {}
            }
            """)
        ),

          new(
            "move_active_window",
            "Moves the currently active/focused window to the specified screen coordinates.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "x": { "type": "integer", "description": "Target X position in screen coordinates" },
                "y": { "type": "integer", "description": "Target Y position in screen coordinates" }
              },
              "required": ["x", "y"]
            }
            """)
        ),

          new(
            "resize_active_window",
            "Resizes the currently active/focused window to the specified width and height.",
            BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "width": { "type": "integer", "description": "Target window width in pixels" },
                "height": { "type": "integer", "description": "Target window height in pixels" }
              },
              "required": ["width", "height"]
            }
            """)
        ),

          new(
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

      public static IReadOnlyList<ChatTool> GetChatTools()
        => FunctionDefinitions
          .Select(static definition => ChatTool.CreateFunctionTool(definition.Name, definition.Description, definition.Parameters))
          .ToList();

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

    /// <summary>Gets a string array from the args dictionary, returning an empty list if missing.</summary>
    public static IReadOnlyList<string> GetStringArray(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                items.Add(item.GetString() ?? string.Empty);
        }

        return items;
    }

  /// <summary>Gets a bool value from the args dictionary, returning a default if missing.</summary>
  public static bool GetBool(Dictionary<string, JsonElement> args, string key, bool defaultValue = false)
    => args.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
      ? v.GetBoolean()
      : defaultValue;
}
