using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

internal readonly record struct ScreenshotAnnotationData(
    WindowBounds CaptureBounds,
    int CursorX,
    int CursorY,
    WindowBounds? SuggestedContentArea = null,
    ScreenshotClickTarget? IntendedClickTarget = null,
    ScreenshotHighlightedRegion? IntendedElementRegion = null)
{
    public bool CursorIsInsideCapture =>
        ContainsPoint(CursorX, CursorY);

    public (int X, int Y) TopLeft => (CaptureBounds.X, CaptureBounds.Y);

    public (int X, int Y) TopRight => (CaptureBounds.X + Math.Max(0, CaptureBounds.Width - 1), CaptureBounds.Y);

    public (int X, int Y) BottomLeft => (CaptureBounds.X, CaptureBounds.Y + Math.Max(0, CaptureBounds.Height - 1));

    public (int X, int Y) BottomRight => (
        CaptureBounds.X + Math.Max(0, CaptureBounds.Width - 1),
        CaptureBounds.Y + Math.Max(0, CaptureBounds.Height - 1));

    public bool HasSuggestedContentArea => SuggestedContentArea is { Width: > 0, Height: > 0 };

    public bool HasIntendedClickTarget => IntendedClickTarget is not null;

    public bool HasIntendedElementRegion => IntendedElementRegion is { Bounds.Width: > 0, Bounds.Height: > 0 };

    public bool IntendedClickIsInsideCapture =>
        IntendedClickTarget is { } target
        && ContainsPoint(target.X, target.Y);

    public bool IntendedElementRegionIntersectsCapture =>
        IntendedElementRegion is { } region
        && Intersects(region.Bounds);

    public bool ContainsPoint(int x, int y)
        => x >= CaptureBounds.X
        && x < CaptureBounds.X + CaptureBounds.Width
        && y >= CaptureBounds.Y
        && y < CaptureBounds.Y + CaptureBounds.Height;

    public bool Intersects(WindowBounds bounds)
    {
        int left = Math.Max(CaptureBounds.X, bounds.X);
        int top = Math.Max(CaptureBounds.Y, bounds.Y);
        int right = Math.Min(CaptureBounds.X + CaptureBounds.Width, bounds.X + bounds.Width);
        int bottom = Math.Min(CaptureBounds.Y + CaptureBounds.Height, bounds.Y + bounds.Height);
        return right > left && bottom > top;
    }

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

internal readonly record struct ScreenshotClickTarget(int X, int Y, string? Label = null);

internal readonly record struct ScreenshotHighlightedRegion(WindowBounds Bounds, string? Label = null);