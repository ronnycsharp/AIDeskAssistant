using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

public readonly record struct WindowHitTestResult(string ApplicationName, string Title, WindowBounds Bounds);
public readonly record struct WindowInfo(string ApplicationName, string Title, WindowBounds Bounds, bool IsFrontmost, bool IsMinimized);

public interface IWindowService
{
    /// <summary>Returns the bounds of the currently active/focused window.</summary>
    WindowBounds GetActiveWindowBounds();

    /// <summary>Returns the topmost visible window that contains the specified screen point, if available.</summary>
    WindowHitTestResult? GetWindowAtPoint(int x, int y);

    /// <summary>Returns the current frontmost application's display name.</summary>
    string GetFrontmostApplicationName();

    /// <summary>Lists known top-level windows for the current desktop session.</summary>
    IReadOnlyList<WindowInfo> ListWindows();

    /// <summary>Focuses a window matching the supplied application name and/or title substring.</summary>
    bool FocusWindow(string? applicationName, string? titleSubstring);

    /// <summary>Moves the active/focused window to the specified screen position.</summary>
    void MoveActiveWindow(int x, int y);

    /// <summary>Resizes the active/focused window to the specified dimensions.</summary>
    void ResizeActiveWindow(int width, int height);
}
