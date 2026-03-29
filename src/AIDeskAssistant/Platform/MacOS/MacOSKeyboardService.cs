using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSKeyboardService : IKeyboardService
{
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGEventPost(int tap, IntPtr evt);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGEventSetFlags(IntPtr evt, ulong flags);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    // macOS virtual key codes for common keys.
    private static readonly Dictionary<string, ushort> VKCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 0x00, ["s"] = 0x01, ["d"] = 0x02, ["f"] = 0x03, ["h"] = 0x04,
        ["g"] = 0x05, ["z"] = 0x06, ["x"] = 0x07, ["c"] = 0x08, ["v"] = 0x09,
        ["b"] = 0x0B, ["q"] = 0x0C, ["w"] = 0x0D, ["e"] = 0x0E, ["r"] = 0x0F,
        ["y"] = 0x10, ["t"] = 0x11, ["1"] = 0x12, ["2"] = 0x13, ["3"] = 0x14,
        ["4"] = 0x15, ["6"] = 0x16, ["5"] = 0x17, ["equal"] = 0x18, ["9"] = 0x19,
        ["7"] = 0x1A, ["minus"] = 0x1B, ["8"] = 0x1C, ["0"] = 0x1D, ["o"] = 0x1F,
        ["u"] = 0x20, ["i"] = 0x22, ["p"] = 0x23, ["enter"] = 0x24, ["return"] = 0x24,
        ["l"] = 0x25, ["j"] = 0x26, ["k"] = 0x28, ["n"] = 0x2D, ["m"] = 0x2E,
        ["tab"] = 0x30, ["space"] = 0x31, ["backspace"] = 0x33, ["delete"] = 0x33,
        ["escape"] = 0x35, ["esc"] = 0x35,
        ["left"] = 0x7B, ["right"] = 0x7C, ["down"] = 0x7D, ["up"] = 0x7E,
        ["f1"] = 0x7A, ["f2"] = 0x78, ["f3"] = 0x63, ["f4"] = 0x76,
        ["f5"] = 0x60, ["f6"] = 0x61, ["f7"] = 0x62, ["f8"] = 0x64,
        ["f9"] = 0x65, ["f10"] = 0x6D, ["f11"] = 0x67, ["f12"] = 0x6F,
    };

    // CGEventFlags for modifier keys.
    private static readonly Dictionary<string, ulong> ModifierFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"]     = 0x100000,
        ["command"] = 0x100000,
        ["ctrl"]    = 0x040000,
        ["control"] = 0x040000,
        ["alt"]     = 0x080000,
        ["option"]  = 0x080000,
        ["shift"]   = 0x020000,
    };

    public void TypeText(string text)
    {
        // Use the osascript approach for typing arbitrary Unicode text.
        var escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script  = $"tell application \"System Events\" to keystroke \"{escaped}\"";
        RunOsascript(script);
    }

    public void PressKey(string keyCombo)
    {
        var parts     = keyCombo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new List<string>();
        string? mainKey = null;

        foreach (var part in parts)
        {
            if (ModifierFlags.ContainsKey(part))
                modifiers.Add(part);
            else
                mainKey ??= part;
        }

        if (mainKey is null) return;

        if (modifiers.Count == 0)
        {
            // Simple key press via CGEvent.
            if (VKCodes.TryGetValue(mainKey, out ushort vk))
            {
                PostKey(vk, 0);
            }
        }
        else
        {
            // Use osascript for modifier combinations (most reliable).
            string modStr = string.Join(", ", modifiers.Select(m => m.ToLowerInvariant() switch
            {
                "cmd" or "command" => "command down",
                "ctrl" or "control" => "control down",
                "alt" or "option" => "option down",
                "shift" => "shift down",
                _ => m
            }));
            var script = $"tell application \"System Events\" to keystroke \"{mainKey}\" using {{{modStr}}}";
            RunOsascript(script);
        }
    }

    private static void PostKey(ushort vk, ulong flags)
    {
        IntPtr down = CGEventCreateKeyboardEvent(IntPtr.Zero, vk, true);
        IntPtr up   = CGEventCreateKeyboardEvent(IntPtr.Zero, vk, false);
        try
        {
            if (flags != 0)
            {
                CGEventSetFlags(down, flags);
                CGEventSetFlags(up,   flags);
            }
            CGEventPost(0, down);
            CGEventPost(0, up);
        }
        finally
        {
            CFRelease(down);
            CFRelease(up);
        }
    }

    private static void RunOsascript(string script)
    {
        // Pass the script via stdin to avoid any shell-argument injection.
        var psi = new System.Diagnostics.ProcessStartInfo("osascript")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return;
        proc.StandardInput.WriteLine(script);
        proc.StandardInput.Close();
        proc.WaitForExit(10_000);
    }
}
