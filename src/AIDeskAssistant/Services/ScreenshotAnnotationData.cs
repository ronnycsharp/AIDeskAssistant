using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

internal readonly record struct ScreenshotAnnotationData(
    WindowBounds CaptureBounds,
    int CursorX,
    int CursorY)
{
    public bool CursorIsInsideCapture =>
        CursorX >= CaptureBounds.X
        && CursorX < CaptureBounds.X + CaptureBounds.Width
        && CursorY >= CaptureBounds.Y
        && CursorY < CaptureBounds.Y + CaptureBounds.Height;

    public (int X, int Y) TopLeft => (CaptureBounds.X, CaptureBounds.Y);

    public (int X, int Y) TopRight => (CaptureBounds.X + Math.Max(0, CaptureBounds.Width - 1), CaptureBounds.Y);

    public (int X, int Y) BottomLeft => (CaptureBounds.X, CaptureBounds.Y + Math.Max(0, CaptureBounds.Height - 1));

    public (int X, int Y) BottomRight => (
        CaptureBounds.X + Math.Max(0, CaptureBounds.Width - 1),
        CaptureBounds.Y + Math.Max(0, CaptureBounds.Height - 1));
}