using AIDeskAssistant.Models;
using SkiaSharp;

namespace AIDeskAssistant.Services;

internal static class ScreenshotCursorDetailExtractor
{
    public const int DefaultLogicalCropSize = 300;
    private const int GridStep = 100;
    private static readonly SKColor GridLineColor = new(255, 255, 255, 92);
    private static readonly SKColor GridMajorLineColor = new(255, 255, 255, 148);
    private static readonly SKColor LabelBackgroundColor = new(18, 18, 18, 210);
    private static readonly SKColor LabelBorderColor = new(255, 255, 255, 230);
    private static readonly SKColor LabelTextColor = SKColors.White;

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

            DrawCoordinateGrid(croppedBitmap, detailBounds);

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

    private static void DrawCoordinateGrid(SKBitmap bitmap, WindowBounds detailBounds)
    {
        using var canvas = new SKCanvas(bitmap);
        float fontSize = Math.Clamp(Math.Min(bitmap.Width, bitmap.Height) / 18f, 10f, 20f);
        using var minorLinePaint = new SKPaint { Color = GridLineColor, IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        using var majorLinePaint = new SKPaint { Color = GridMajorLineColor, IsAntialias = true, StrokeWidth = 2f, Style = SKPaintStyle.Stroke };

        int startX = AlignDown(detailBounds.X, GridStep);
        int endX = detailBounds.X + Math.Max(0, detailBounds.Width - 1);
        for (int globalX = startX; globalX <= endX; globalX += GridStep)
        {
            if (globalX < detailBounds.X)
                continue;

            float localX = MapGlobalXToLocal(detailBounds, globalX, bitmap.Width);
            canvas.DrawLine(localX, 0, localX, bitmap.Height - 1, majorLinePaint);
            DrawLabel(canvas, globalX.ToString(), new SKPoint(Math.Min(bitmap.Width - 8f, localX + 4f), 6f), fontSize);
        }

        int startY = AlignDown(detailBounds.Y, GridStep);
        int endY = detailBounds.Y + Math.Max(0, detailBounds.Height - 1);
        for (int globalY = startY; globalY <= endY; globalY += GridStep)
        {
            if (globalY < detailBounds.Y)
                continue;

            float localY = MapGlobalYToLocal(detailBounds, globalY, bitmap.Height);
            canvas.DrawLine(0, localY, bitmap.Width - 1, localY, majorLinePaint);
            DrawLabel(canvas, globalY.ToString(), new SKPoint(6f, Math.Min(bitmap.Height - 8f, localY + 4f)), fontSize);
        }

        float centerX = MapGlobalXToLocal(detailBounds, detailBounds.X + (detailBounds.Width / 2), bitmap.Width);
        float centerY = MapGlobalYToLocal(detailBounds, detailBounds.Y + (detailBounds.Height / 2), bitmap.Height);
        canvas.DrawLine(centerX, 0, centerX, bitmap.Height - 1, minorLinePaint);
        canvas.DrawLine(0, centerY, bitmap.Width - 1, centerY, minorLinePaint);
    }

    private static float MapGlobalXToLocal(WindowBounds detailBounds, int globalX, int imageWidth)
    {
        double relativeX = detailBounds.Width <= 1 ? 0d : (double)(globalX - detailBounds.X) / (detailBounds.Width - 1);
        float x = (float)Math.Round(relativeX * Math.Max(0, imageWidth - 1));
        return Math.Clamp(x, 0, Math.Max(0, imageWidth - 1));
    }

    private static float MapGlobalYToLocal(WindowBounds detailBounds, int globalY, int imageHeight)
    {
        double relativeY = detailBounds.Height <= 1 ? 0d : (double)(globalY - detailBounds.Y) / (detailBounds.Height - 1);
        float y = (float)Math.Round(relativeY * Math.Max(0, imageHeight - 1));
        return Math.Clamp(y, 0, Math.Max(0, imageHeight - 1));
    }

    private static void DrawLabel(SKCanvas canvas, string text, SKPoint origin, float fontSize)
    {
        using var textPaint = new SKPaint
        {
            Color = LabelTextColor,
            IsAntialias = true,
            TextSize = fontSize,
            Typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold),
        };

        SKRect textBounds = default;
        textPaint.MeasureText(text, ref textBounds);
        SKRect badgeRect = new(origin.X, origin.Y, origin.X + textBounds.Width + 10f, origin.Y + textBounds.Height + 8f);
        badgeRect = ClampRectangle(badgeRect, canvas.LocalClipBounds.Width, canvas.LocalClipBounds.Height);
        var roundRect = new SKRoundRect(badgeRect, 6f, 6f);
        using var backgroundPaint = new SKPaint { Color = LabelBackgroundColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = LabelBorderColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        canvas.DrawRoundRect(roundRect, backgroundPaint);
        canvas.DrawRoundRect(roundRect, borderPaint);

        SKFontMetrics fontMetrics = textPaint.FontMetrics;
        float baseline = badgeRect.Top + ((badgeRect.Height - (fontMetrics.Descent - fontMetrics.Ascent)) / 2f) - fontMetrics.Ascent;
        canvas.DrawText(text, badgeRect.Left + 5f, baseline, textPaint);
    }

    private static SKRect ClampRectangle(SKRect rect, float imageWidth, float imageHeight)
    {
        float x = Math.Clamp(rect.Left, 4f, Math.Max(4f, imageWidth - rect.Width - 4f));
        float y = Math.Clamp(rect.Top, 4f, Math.Max(4f, imageHeight - rect.Height - 4f));
        return new SKRect(x, y, x + rect.Width, y + rect.Height);
    }

    private static int AlignDown(int value, int step)
        => step <= 0 ? value : value - PositiveModulo(value, step);

    private static int PositiveModulo(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}

internal sealed record ScreenshotCursorDetail(WindowBounds Bounds, byte[] Bytes, string MediaType, int Width, int Height);