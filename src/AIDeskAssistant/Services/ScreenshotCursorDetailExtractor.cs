using AIDeskAssistant.Models;
using SkiaSharp;

namespace AIDeskAssistant.Services;

internal static class ScreenshotCursorDetailExtractor
{
    public const int DefaultLogicalCropSize = 200;

    public static ScreenshotCursorDetail? Create(byte[] screenshotBytes, ScreenshotAnnotationData annotation, int logicalCropSize = DefaultLogicalCropSize)
    {
        try
        {
            using SKBitmap? sourceBitmap = SKBitmap.Decode(screenshotBytes);
            if (sourceBitmap is null)
                return null;

            WindowBounds detailBounds = CreateBounds(annotation, logicalCropSize);
            if (detailBounds.Width <= 0 || detailBounds.Height <= 0)
                return null;

            int left = MapGlobalXToImage(annotation, detailBounds.X, sourceBitmap.Width);
            int top = MapGlobalYToImage(annotation, detailBounds.Y, sourceBitmap.Height);
            int right = MapGlobalXToImage(annotation, detailBounds.X + Math.Max(0, detailBounds.Width - 1), sourceBitmap.Width);
            int bottom = MapGlobalYToImage(annotation, detailBounds.Y + Math.Max(0, detailBounds.Height - 1), sourceBitmap.Height);

            int cropX = Math.Max(0, Math.Min(left, right));
            int cropY = Math.Max(0, Math.Min(top, bottom));
            int cropWidth = Math.Max(1, Math.Abs(right - left) + 1);
            int cropHeight = Math.Max(1, Math.Abs(bottom - top) + 1);

            cropWidth = Math.Min(cropWidth, sourceBitmap.Width - cropX);
            cropHeight = Math.Min(cropHeight, sourceBitmap.Height - cropY);

            var subset = new SKRectI(cropX, cropY, cropX + cropWidth, cropY + cropHeight);
            using SKBitmap croppedBitmap = new(cropWidth, cropHeight, sourceBitmap.ColorType, sourceBitmap.AlphaType);
            if (!sourceBitmap.ExtractSubset(croppedBitmap, subset))
                return null;

            using SKImage image = SKImage.FromBitmap(croppedBitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new ScreenshotCursorDetail(detailBounds, data.ToArray(), "image/png", cropWidth, cropHeight);
        }
        catch
        {
            return null;
        }
    }

    public static WindowBounds CreateBounds(ScreenshotAnnotationData annotation, int logicalCropSize = DefaultLogicalCropSize)
    {
        int width = Math.Min(logicalCropSize, Math.Max(1, annotation.CaptureBounds.Width));
        int height = Math.Min(logicalCropSize, Math.Max(1, annotation.CaptureBounds.Height));
        int maxX = annotation.CaptureBounds.X + Math.Max(0, annotation.CaptureBounds.Width - width);
        int maxY = annotation.CaptureBounds.Y + Math.Max(0, annotation.CaptureBounds.Height - height);

        int x = Math.Clamp(annotation.CursorX - (width / 2), annotation.CaptureBounds.X, maxX);
        int y = Math.Clamp(annotation.CursorY - (height / 2), annotation.CaptureBounds.Y, maxY);
        return new WindowBounds(x, y, width, height);
    }

    private static int MapGlobalXToImage(ScreenshotAnnotationData annotation, int globalX, int imageWidth)
    {
        double relativeX = annotation.CaptureBounds.Width <= 1 ? 0d : (double)(globalX - annotation.CaptureBounds.X) / (annotation.CaptureBounds.Width - 1);
        return Math.Clamp((int)Math.Round(relativeX * Math.Max(0, imageWidth - 1)), 0, Math.Max(0, imageWidth - 1));
    }

    private static int MapGlobalYToImage(ScreenshotAnnotationData annotation, int globalY, int imageHeight)
    {
        double relativeY = annotation.CaptureBounds.Height <= 1 ? 0d : (double)(globalY - annotation.CaptureBounds.Y) / (annotation.CaptureBounds.Height - 1);
        return Math.Clamp((int)Math.Round(relativeY * Math.Max(0, imageHeight - 1)), 0, Math.Max(0, imageHeight - 1));
    }
}

internal sealed record ScreenshotCursorDetail(WindowBounds Bounds, byte[] Bytes, string MediaType, int Width, int Height);