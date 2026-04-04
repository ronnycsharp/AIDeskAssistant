using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowService : IWindowService
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    public WindowBounds GetActiveWindowBounds()
    {
        nint handle = RequireForegroundWindow();
        if (!GetWindowRect(handle, out Rect rect))
            throw new InvalidOperationException($"Failed to query active window bounds (Win32 error {Marshal.GetLastWin32Error()}).");

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public WindowHitTestResult? GetWindowAtPoint(int x, int y)
    {
        nint handle = WindowFromPoint(new Point { X = x, Y = y });
        if (handle == 0)
            return null;

        if (!GetWindowRect(handle, out Rect rect))
            throw new InvalidOperationException($"Failed to query window at point ({x}, {y}) (Win32 error {Marshal.GetLastWin32Error()}).");

        var titleBuilder = new StringBuilder(512);
        _ = GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
        _ = GetWindowThreadProcessId(handle, out uint processId);

        string applicationName;
        try
        {
            applicationName = processId == 0 ? string.Empty : System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            applicationName = string.Empty;
        }

        return new WindowHitTestResult(
            applicationName,
            titleBuilder.ToString(),
            new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    public void MoveActiveWindow(int x, int y)
    {
        nint handle = RequireForegroundWindow();
        if (!SetWindowPos(handle, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
            throw new InvalidOperationException($"Failed to move active window (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    public void ResizeActiveWindow(int width, int height)
    {
        nint handle = RequireForegroundWindow();
        if (!SetWindowPos(handle, 0, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
            throw new InvalidOperationException($"Failed to resize active window (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private static nint RequireForegroundWindow()
    {
        nint handle = GetForegroundWindow();
        if (handle == 0)
            throw new InvalidOperationException("No active window is currently available.");
        return handle;
    }
}
