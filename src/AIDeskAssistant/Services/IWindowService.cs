using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

public readonly record struct WindowHitTestResult(string ApplicationName, string Title, WindowBounds Bounds);

public interface IWindowService
{
    /// <summary>Returns the bounds of the currently active/focused window.</summary>
    WindowBounds GetActiveWindowBounds();

    /// <summary>Returns the topmost visible window that contains the specified screen point, if available.</summary>
    WindowHitTestResult? GetWindowAtPoint(int x, int y);

    /// <summary>Moves the active/focused window to the specified screen position.</summary>
    void MoveActiveWindow(int x, int y);

    /// <summary>Resizes the active/focused window to the specified dimensions.</summary>
    void ResizeActiveWindow(int width, int height);
}
