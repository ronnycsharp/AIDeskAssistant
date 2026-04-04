using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowService : IWindowService
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

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
    private const int SW_RESTORE = 9;

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

    public string GetFrontmostApplicationName()
    {
        nint handle = RequireForegroundWindow();
        return GetApplicationName(handle);
    }

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        nint foregroundHandle = GetForegroundWindow();
        var windows = new List<WindowInfo>();

        _ = EnumWindows((handle, _) =>
        {
            if (!TryCreateWindowInfo(handle, foregroundHandle, out WindowInfo? info) || info is null)
                return true;

            windows.Add(info.Value);
            return true;
        }, 0);

        return windows;
    }

    public bool FocusWindow(string? applicationName, string? titleSubstring)
    {
        string normalizedApplicationName = Normalize(applicationName);
        string normalizedTitleSubstring = Normalize(titleSubstring);

        WindowInfo? match = ListWindows().FirstOrDefault(window => MatchesWindow(window, normalizedApplicationName, normalizedTitleSubstring));
        if (match is null)
            return false;

        nint? handle = FindMatchingHandle(match.Value);
        if (!handle.HasValue)
            return false;

        if (IsIconic(handle.Value))
            _ = ShowWindow(handle.Value, SW_RESTORE);

        return SetForegroundWindow(handle.Value);
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

    private static bool TryCreateWindowInfo(nint handle, nint foregroundHandle, out WindowInfo? info)
    {
        info = null;

        if (handle == 0 || !IsWindowVisible(handle) || !GetWindowRect(handle, out Rect rect))
            return false;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return false;

        string title = GetWindowTitle(handle);
        string applicationName = GetApplicationName(handle);
        bool isMinimized = IsIconic(handle);

        info = new WindowInfo(
            applicationName,
            title,
            new WindowBounds(rect.Left, rect.Top, width, height),
            handle == foregroundHandle,
            isMinimized);
        return true;
    }

    private static string GetWindowTitle(nint handle)
    {
        var titleBuilder = new StringBuilder(512);
        _ = GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
        return titleBuilder.ToString();
    }

    private static string GetApplicationName(nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out uint processId);

        try
        {
            return processId == 0 ? string.Empty : System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static bool MatchesWindow(WindowInfo window, string normalizedApplicationName, string normalizedTitleSubstring)
    {
        if (window.IsMinimized)
            return false;

        if (!string.IsNullOrEmpty(normalizedApplicationName))
        {
            string applicationName = Normalize(window.ApplicationName);
            if (!applicationName.Contains(normalizedApplicationName, StringComparison.Ordinal))
                return false;
        }

        if (!string.IsNullOrEmpty(normalizedTitleSubstring))
        {
            string title = Normalize(window.Title);
            if (!title.Contains(normalizedTitleSubstring, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static nint? FindMatchingHandle(WindowInfo match)
    {
        nint? result = null;
        _ = EnumWindows((handle, _) =>
        {
            if (!TryCreateWindowInfo(handle, GetForegroundWindow(), out WindowInfo? info) || info is null)
                return true;

            if (info.Value.ApplicationName == match.ApplicationName
                && info.Value.Title == match.Title
                && info.Value.Bounds == match.Bounds)
            {
                result = handle;
                return false;
            }

            return true;
        }, 0);

        return result;
    }
}
