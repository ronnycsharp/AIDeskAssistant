using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowService : IWindowService
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

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

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public WindowBounds GetActiveWindowBounds()
    {
        nint handle = RequireForegroundWindow();
        if (!GetWindowRect(handle, out RECT rect))
            throw new InvalidOperationException($"Failed to query active window bounds (Win32 error {Marshal.GetLastWin32Error()}).");

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
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
