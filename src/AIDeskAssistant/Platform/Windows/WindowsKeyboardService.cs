using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsKeyboardService : IKeyboardService
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP   = 0x0002;

    // Virtual key codes for common special keys.
    private static readonly Dictionary<string, byte> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"]     = 0x0D, ["return"]   = 0x0D,
        ["tab"]       = 0x09,
        ["esc"]       = 0x1B, ["escape"]   = 0x1B,
        ["space"]     = 0x20,
        ["backspace"] = 0x08,
        ["delete"]    = 0x2E, ["del"]      = 0x2E,
        ["home"]      = 0x24, ["end"]      = 0x23,
        ["pageup"]    = 0x21, ["pagedown"] = 0x22,
        ["up"]        = 0x26, ["down"]     = 0x28,
        ["left"]      = 0x25, ["right"]    = 0x27,
        ["f1"]  = 0x70, ["f2"]  = 0x71, ["f3"]  = 0x72, ["f4"]  = 0x73,
        ["f5"]  = 0x74, ["f6"]  = 0x75, ["f7"]  = 0x76, ["f8"]  = 0x77,
        ["f9"]  = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["ctrl"]  = 0x11, ["control"] = 0x11,
        ["alt"]   = 0x12,
        ["shift"] = 0x10,
        ["win"]   = 0x5B, ["cmd"]  = 0x5B,
        ["a"] = 0x41, ["b"] = 0x42, ["c"] = 0x43, ["d"] = 0x44, ["e"] = 0x45,
        ["f"] = 0x46, ["g"] = 0x47, ["h"] = 0x48, ["i"] = 0x49, ["j"] = 0x4A,
        ["k"] = 0x4B, ["l"] = 0x4C, ["m"] = 0x4D, ["n"] = 0x4E, ["o"] = 0x4F,
        ["p"] = 0x50, ["q"] = 0x51, ["r"] = 0x52, ["s"] = 0x53, ["t"] = 0x54,
        ["u"] = 0x55, ["v"] = 0x56, ["w"] = 0x57, ["x"] = 0x58, ["y"] = 0x59,
        ["z"] = 0x5A,
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
    };

    private const int KeyPressDelayMs = 10;

    public void TypeText(string text)
    {
        foreach (char ch in text)
        {
            short vk = VkKeyScan(ch);
            if (vk == -1) continue;
            byte vkCode = (byte)(vk & 0xFF);
            byte shiftState = (byte)((vk >> 8) & 0xFF);
            bool needShift = (shiftState & 0x01) != 0;
            if (needShift) keybd_event(0x10, 0, KEYEVENTF_KEYDOWN, 0);
            keybd_event(vkCode, 0, KEYEVENTF_KEYDOWN, 0);
            keybd_event(vkCode, 0, KEYEVENTF_KEYUP,   0);
            if (needShift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, 0);
            Thread.Sleep(KeyPressDelayMs);
        }
    }

    public void PressKey(string keyCombo)
    {
        var parts = keyCombo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var vkCodes = new List<byte>();
        foreach (var part in parts)
        {
            if (SpecialKeys.TryGetValue(part, out byte vk))
                vkCodes.Add(vk);
        }

        // Press all keys down, then release in reverse order.
        foreach (byte vk in vkCodes)
            keybd_event(vk, 0, KEYEVENTF_KEYDOWN, 0);
        for (int i = vkCodes.Count - 1; i >= 0; i--)
            keybd_event(vkCodes[i], 0, KEYEVENTF_KEYUP, 0);
    }
}
