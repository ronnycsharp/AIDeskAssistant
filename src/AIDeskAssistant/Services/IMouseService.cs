using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

public interface IMouseService
{
    /// <summary>Moves the mouse cursor to the specified screen coordinates using a smooth eased path.</summary>
    void MoveTo(int x, int y);

    /// <summary>Clicks the specified mouse button at the current cursor position.</summary>
    void Click(MouseButton button = MouseButton.Left);

    /// <summary>Moves to (x, y) then clicks the specified button.</summary>
    void ClickAt(int x, int y, MouseButton button = MouseButton.Left);

    /// <summary>Double-clicks the left mouse button at the current cursor position.</summary>
    void DoubleClick(int x, int y);

    /// <summary>Scrolls the mouse wheel at the current position.</summary>
    /// <param name="delta">Positive values scroll up; negative values scroll down.</param>
    void Scroll(int delta);

    /// <summary>Returns the current cursor position.</summary>
    (int X, int Y) GetPosition();
}
