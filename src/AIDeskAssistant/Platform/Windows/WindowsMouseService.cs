using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsMouseService : IMouseService
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, nint dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    private const uint MOUSEEVENTF_WHEEL      = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    private const int ClickDelayMs       = 30;
    private const int DoubleClickDelayMs = 50;

    public void MoveTo(int x, int y)
    {
        foreach (var point in MouseMotion.CreateEasedPath(GetPosition(), (x, y)))
        {
            SetCursorPos(point.X, point.Y);
            Thread.Sleep(MouseMotion.StepDelayMs);
        }
    }

    public void Click(MouseButton button = MouseButton.Left)
    {
        (uint down, uint up) = button switch
        {
            MouseButton.Right  => (MOUSEEVENTF_RIGHTDOWN,  MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _                  => (MOUSEEVENTF_LEFTDOWN,   MOUSEEVENTF_LEFTUP),
        };
        mouse_event(down, 0, 0, 0, 0);
        mouse_event(up,   0, 0, 0, 0);
    }

    public void Drag(int startX, int startY, int endX, int endY, MouseButton button = MouseButton.Left)
    {
        (uint down, uint up) = button switch
        {
            MouseButton.Right  => (MOUSEEVENTF_RIGHTDOWN,  MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _                  => (MOUSEEVENTF_LEFTDOWN,   MOUSEEVENTF_LEFTUP),
        };

        MoveTo(startX, startY);
        Thread.Sleep(ClickDelayMs);
        mouse_event(down, 0, 0, 0, 0);

        foreach (var point in MouseMotion.CreateEasedPath((startX, startY), (endX, endY)))
        {
            SetCursorPos(point.X, point.Y);
            Thread.Sleep(MouseMotion.StepDelayMs);
        }

        mouse_event(up, 0, 0, 0, 0);
    }

    public void ClickAt(int x, int y, MouseButton button = MouseButton.Left)
    {
        MoveTo(x, y);
        Thread.Sleep(ClickDelayMs);
        Click(button);
    }

    public void DoubleClick(int x, int y)
    {
        MoveTo(x, y);
        Thread.Sleep(ClickDelayMs);
        Click();
        Thread.Sleep(DoubleClickDelayMs);
        Click();
    }

    public void Scroll(int delta)
    {
        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta * 120, 0);
    }

    public (int X, int Y) GetPosition()
    {
        GetCursorPos(out Point p);
        return (p.X, p.Y);
    }
}
