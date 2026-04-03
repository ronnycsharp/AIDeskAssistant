using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

internal readonly record struct ScreenshotAnnotationData(
    WindowBounds CaptureBounds,
    int CursorX,
    int CursorY,
    WindowBounds? SuggestedContentArea = null)
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

    public bool HasSuggestedContentArea => SuggestedContentArea is { Width: > 0, Height: > 0 };

    public static WindowBounds CreateSuggestedContentArea(WindowBounds captureBounds)
    {
        int horizontalInset = Math.Clamp((int)Math.Round(captureBounds.Width * 0.04), 24, 96);
        int topInset = Math.Clamp((int)Math.Round(captureBounds.Height * 0.14), 56, 180);
        int bottomInset = Math.Clamp((int)Math.Round(captureBounds.Height * 0.05), 24, 96);

        int width = Math.Max(0, captureBounds.Width - (horizontalInset * 2));
        int height = Math.Max(0, captureBounds.Height - topInset - bottomInset);

        return new WindowBounds(
            captureBounds.X + horizontalInset,
            captureBounds.Y + topInset,
            width,
            height);
    }
}