using AIDeskAssistant.Models;
using SkiaSharp;

namespace AIDeskAssistant.Services;

internal static class ScreenshotAnnotator
{
    private static readonly SKColor IntendedClickColor = new(255, 64, 64, 255);
    private static readonly SKColor IntendedElementColor = new(255, 59, 48, 255);
    private static readonly SKColor AxMarkColor = new(79, 195, 247, 255);
    private static readonly SKColor OcrMarkColor = new(255, 99, 132, 255);
    private static readonly SKColor LabelBackgroundColor = new(18, 18, 18, 210);
    private static readonly SKColor LabelBorderColor = new(255, 255, 255, 235);
    private static readonly SKColor LabelTextColor = SKColors.White;

    public static byte[] Annotate(byte[] screenshotBytes, ScreenshotAnnotationData annotation)
    {
        try
        {
            using SKBitmap? sourceBitmap = SKBitmap.Decode(screenshotBytes);
            if (sourceBitmap is null)
                return screenshotBytes;

            using var surface = SKSurface.Create(new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
                return screenshotBytes;

            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            DrawBaseImage(canvas, sourceBitmap, annotation);

            float fontSize = CreateFontSize(sourceBitmap.Width, sourceBitmap.Height);
            var metrics = new ImageMetrics(sourceBitmap.Width, sourceBitmap.Height, fontSize);

            if (string.Equals(annotation.VisualStyle, ScreenshotVisualStyles.SchematicTarget, StringComparison.Ordinal))
            {
                DrawIntendedElementRegion(canvas, metrics, annotation);
                DrawIntendedClickTarget(canvas, metrics, annotation);
            }
            else if (annotation.HasMarks)
            {
                DrawNumberedMarks(canvas, metrics, annotation);
            }

            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            return screenshotBytes;
        }
    }

    private static void DrawBaseImage(SKCanvas canvas, SKBitmap sourceBitmap, ScreenshotAnnotationData annotation)
    {
        if (!string.Equals(annotation.VisualStyle, ScreenshotVisualStyles.SchematicTarget, StringComparison.Ordinal))
        {
            canvas.DrawBitmap(sourceBitmap, 0, 0);
            return;
        }

        using SKBitmap grayscaleBitmap = CreateGrayscaleBitmap(sourceBitmap);
        canvas.DrawBitmap(grayscaleBitmap, 0, 0);

        using var dimPaint = new SKPaint { Color = new SKColor(0, 0, 0, 132), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawRect(SKRect.Create(sourceBitmap.Width, sourceBitmap.Height), dimPaint);

        if (annotation.HasIntendedElementRegion && annotation.IntendedElementRegion is ScreenshotHighlightedRegion region && annotation.IntendedElementRegionIntersectsCapture)
        {
            SKRect rect = MapBoundsToImageRect(annotation, region.Bounds, sourceBitmap.Width, sourceBitmap.Height);
            RestoreTargetRegion(canvas, sourceBitmap, rect, 8f);
        }
        else if (annotation.HasIntendedClickTarget && annotation.IntendedClickTarget is ScreenshotClickTarget clickTarget && annotation.IntendedClickIsInsideCapture)
        {
            SKPoint point = MapGlobalPointToImage(annotation, clickTarget.X, clickTarget.Y, sourceBitmap.Width, sourceBitmap.Height);
            float radius = Math.Clamp(Math.Min(sourceBitmap.Width, sourceBitmap.Height) / 9f, 28f, 72f);
            RestoreTargetRegion(canvas, sourceBitmap, SKRect.Create(point.X - radius, point.Y - radius, radius * 2f, radius * 2f), radius * 0.18f);
        }
    }

    private static SKBitmap CreateGrayscaleBitmap(SKBitmap sourceBitmap)
    {
        var grayscaleBitmap = new SKBitmap(sourceBitmap.Width, sourceBitmap.Height, sourceBitmap.ColorType, sourceBitmap.AlphaType);
        for (int y = 0; y < sourceBitmap.Height; y++)
        {
            for (int x = 0; x < sourceBitmap.Width; x++)
            {
                SKColor pixel = sourceBitmap.GetPixel(x, y);
                byte luminance = (byte)Math.Clamp(
                    (int)Math.Round((pixel.Red * 0.2126) + (pixel.Green * 0.7152) + (pixel.Blue * 0.0722)),
                    0,
                    255);
                grayscaleBitmap.SetPixel(x, y, new SKColor(luminance, luminance, luminance, pixel.Alpha));
            }
        }

        return grayscaleBitmap;
    }

    private static void RestoreTargetRegion(SKCanvas canvas, SKBitmap sourceBitmap, SKRect targetRect, float cornerRadius)
    {
        SKRect boundedRect = new(
            Math.Max(0, targetRect.Left),
            Math.Max(0, targetRect.Top),
            Math.Min(sourceBitmap.Width, targetRect.Right),
            Math.Min(sourceBitmap.Height, targetRect.Bottom));

        if (boundedRect.Width <= 0 || boundedRect.Height <= 0)
            return;

        canvas.Save();
        using var clipPath = new SKPath();
        clipPath.AddRoundRect(boundedRect, cornerRadius, cornerRadius);
        canvas.ClipPath(clipPath, antialias: true);
        canvas.DrawBitmap(sourceBitmap, 0, 0);
        canvas.Restore();
    }

    private static SKRect MapBoundsToImageRect(ScreenshotAnnotationData annotation, WindowBounds bounds, int imageWidth, int imageHeight)
    {
        int right = bounds.X + Math.Max(0, bounds.Width - 1);
        int bottom = bounds.Y + Math.Max(0, bounds.Height - 1);
        SKPoint topLeft = MapGlobalPointToImage(annotation, bounds.X, bounds.Y, imageWidth, imageHeight);
        SKPoint bottomRight = MapGlobalPointToImage(annotation, right, bottom, imageWidth, imageHeight);
        return NormalizeRect(new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y));
    }

    private static float CreateFontSize(int imageWidth, int imageHeight)
        => Math.Clamp(Math.Min(imageWidth, imageHeight) / 36f, 12f, 24f);

    private static void DrawIntendedClickTarget(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        if (!annotation.HasIntendedClickTarget || annotation.IntendedClickTarget is not ScreenshotClickTarget clickTarget)
            return;

        if (!annotation.IntendedClickIsInsideCapture)
            return;

        if (annotation.HasIntendedElementRegion)
            return;

        SKPoint targetPoint = MapGlobalPointToImage(annotation, clickTarget.X, clickTarget.Y, metrics.Width, metrics.Height);
        float outerRadius = Math.Clamp(Math.Min(metrics.Width, metrics.Height) / 22f, 18f, 36f);
        using var strokePaint = new SKPaint { Color = IntendedClickColor, IsAntialias = true, StrokeWidth = Math.Max(3f, outerRadius / 7f), Style = SKPaintStyle.Stroke };
        using var haloPaint = new SKPaint { Color = IntendedClickColor.WithAlpha(48), IsAntialias = true, StrokeWidth = Math.Max(8f, outerRadius / 2.8f), Style = SKPaintStyle.Stroke };

        canvas.DrawCircle(targetPoint, outerRadius, haloPaint);
        canvas.DrawCircle(targetPoint, outerRadius, strokePaint);
    }

    private static void DrawIntendedElementRegion(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        if (!annotation.HasIntendedElementRegion || annotation.IntendedElementRegion is not ScreenshotHighlightedRegion region)
            return;

        if (!annotation.IntendedElementRegionIntersectsCapture)
            return;

        int right = region.Bounds.X + Math.Max(0, region.Bounds.Width - 1);
        int bottom = region.Bounds.Y + Math.Max(0, region.Bounds.Height - 1);
        SKPoint topLeft = MapGlobalPointToImage(annotation, region.Bounds.X, region.Bounds.Y, metrics.Width, metrics.Height);
        SKPoint bottomRight = MapGlobalPointToImage(annotation, right, bottom, metrics.Width, metrics.Height);
        SKRect rect = NormalizeRect(new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y));

        using var fillPaint = new SKPaint { Color = IntendedElementColor.WithAlpha(32), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outerPaint = new SKPaint { Color = IntendedElementColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f };
        using var innerPaint = new SKPaint { Color = LabelBorderColor.WithAlpha(220), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

        canvas.DrawRect(rect, fillPaint);
        canvas.DrawRect(rect, outerPaint);
        canvas.DrawRect(new SKRect(rect.Left + 3f, rect.Top + 3f, rect.Right - 3f, rect.Bottom - 3f), innerPaint);
    }

    private static void DrawNumberedMarks(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        if (!annotation.HasMarks)
            return;

        foreach (ScreenshotMark mark in annotation.VisibleMarks)
        {
            int right = mark.Bounds.X + Math.Max(0, mark.Bounds.Width - 1);
            int bottom = mark.Bounds.Y + Math.Max(0, mark.Bounds.Height - 1);
            SKPoint topLeft = MapGlobalPointToImage(annotation, mark.Bounds.X, mark.Bounds.Y, metrics.Width, metrics.Height);
            SKPoint bottomRight = MapGlobalPointToImage(annotation, right, bottom, metrics.Width, metrics.Height);
            SKRect rect = NormalizeRect(new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y));
            SKColor accentColor = string.Equals(mark.Source, "ocr", StringComparison.OrdinalIgnoreCase) ? OcrMarkColor : AxMarkColor;

            using var outerPaint = new SKPaint { Color = accentColor.WithAlpha(230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
            using var fillPaint = new SKPaint { Color = accentColor.WithAlpha(26), IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRect(rect, fillPaint);
            canvas.DrawRect(rect, outerPaint);

            float badgeDiameter = Math.Clamp(Math.Min(metrics.Width, metrics.Height) / 18f, 22f, 34f);
            SKPoint badgeCenter = new(Math.Max(badgeDiameter, rect.Left + 8f), Math.Max(badgeDiameter, rect.Top + 8f));
            using var badgeFill = new SKPaint { Color = accentColor, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var badgeStroke = new SKPaint { Color = LabelBorderColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
            using var numberPaint = CreateTextPaint(Math.Max(10f, metrics.FontSize * 0.78f));
            numberPaint.TextAlign = SKTextAlign.Center;

            canvas.DrawCircle(badgeCenter, badgeDiameter / 2f, badgeFill);
            canvas.DrawCircle(badgeCenter, badgeDiameter / 2f, badgeStroke);

            SKFontMetrics fontMetrics = numberPaint.FontMetrics;
            float baseline = badgeCenter.Y - ((fontMetrics.Ascent + fontMetrics.Descent) / 2f);
            canvas.DrawText(mark.Id.ToString(), badgeCenter.X, baseline, numberPaint);

            string label = $"Mark {mark.Id} ({mark.Source}): {mark.Label}";
            SKPoint labelOrigin = new(Math.Min(metrics.Width - 18f, rect.Left + badgeDiameter + 12f), Math.Max(18f, rect.Top + 10f));
            DrawLabelWithAnchor(canvas, metrics, label, labelOrigin, new SKPoint(rect.MidX, rect.Top), CornerAnchor.TopLeft, accentColor);
        }
    }

    private static void DrawLabelWithAnchor(SKCanvas canvas, ImageMetrics metrics, string text, SKPoint preferredOrigin, SKPoint anchorPoint, CornerAnchor anchor, SKColor accentColor)
    {
        using var textPaint = CreateTextPaint(metrics.FontSize);
        SKRect textBounds = default;
        textPaint.MeasureText(text, ref textBounds);
        float width = textBounds.Width + 20f;
        float height = textBounds.Height + 18f;

        float x = anchor switch
        {
            CornerAnchor.TopRight or CornerAnchor.BottomRight => preferredOrigin.X - width,
            _ => preferredOrigin.X,
        };

        float y = anchor switch
        {
            CornerAnchor.BottomLeft or CornerAnchor.BottomRight => preferredOrigin.Y - height,
            _ => preferredOrigin.Y,
        };

        SKRect rect = ClampRectangle(new SKRect(x, y, x + width, y + height), metrics.Width, metrics.Height);

        using var connectorPaint = new SKPaint { Color = accentColor, IsAntialias = true, StrokeWidth = 3f, Style = SKPaintStyle.Stroke };
        SKPoint rectAnchor = GetNearestPointOnRectangle(rect, anchorPoint);
        canvas.DrawLine(anchorPoint, rectAnchor, connectorPaint);
        DrawBadge(canvas, textPaint, text, rect, accentColor, metrics);
    }

    private static void DrawBadge(SKCanvas canvas, SKPaint textPaint, string text, SKRect rect, SKColor accentColor, ImageMetrics metrics, BadgeStyle? badgeStyle = null)
    {
        BadgeStyle style = badgeStyle ?? new BadgeStyle(null, 2f, 10f);
        rect = ClampRectangle(rect, metrics.Width, metrics.Height);
        using var backgroundPaint = new SKPaint { Color = style.BackgroundColorOverride ?? LabelBackgroundColor, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var borderPaint = new SKPaint { Color = accentColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = style.BorderWidth };
        using var innerBorderPaint = new SKPaint { Color = LabelBorderColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        var roundRect = new SKRoundRect(rect, style.Radius, style.Radius);

        canvas.DrawRoundRect(roundRect, backgroundPaint);
        canvas.DrawRoundRect(roundRect, borderPaint);
        canvas.DrawRoundRect(roundRect, innerBorderPaint);

        SKFontMetrics fontMetrics = textPaint.FontMetrics;
        float baseline = rect.Top + ((rect.Height - (fontMetrics.Descent - fontMetrics.Ascent)) / 2f) - fontMetrics.Ascent;
        canvas.DrawText(text, rect.Left + 10f, baseline, textPaint);
    }

    private static SKRect ClampRectangle(SKRect rect, int imageWidth, int imageHeight)
    {
        float x = Math.Clamp(rect.Left, 8f, Math.Max(8f, imageWidth - rect.Width - 8f));
        float y = Math.Clamp(rect.Top, 8f, Math.Max(8f, imageHeight - rect.Height - 8f));
        return new SKRect(x, y, x + rect.Width, y + rect.Height);
    }

    private static SKRect NormalizeRect(SKRect rect)
    {
        float left = Math.Min(rect.Left, rect.Right);
        float right = Math.Max(rect.Left, rect.Right);
        float top = Math.Min(rect.Top, rect.Bottom);
        float bottom = Math.Max(rect.Top, rect.Bottom);
        return new SKRect(left, top, right, bottom);
    }

    private static SKPoint GetNearestPointOnRectangle(SKRect rect, SKPoint point)
    {
        float x = Math.Clamp(point.X, rect.Left, rect.Right);
        float y = Math.Clamp(point.Y, rect.Top, rect.Bottom);
        return new SKPoint(x, y);
    }

    private static SKPoint MapGlobalPointToImage(ScreenshotAnnotationData annotation, int globalX, int globalY, int imageWidth, int imageHeight)
    {
        double relativeX = annotation.CaptureBounds.Width <= 1 ? 0d : (double)(globalX - annotation.CaptureBounds.X) / (annotation.CaptureBounds.Width - 1);
        double relativeY = annotation.CaptureBounds.Height <= 1 ? 0d : (double)(globalY - annotation.CaptureBounds.Y) / (annotation.CaptureBounds.Height - 1);

        float x = (float)Math.Round(relativeX * Math.Max(0, imageWidth - 1));
        float y = (float)Math.Round(relativeY * Math.Max(0, imageHeight - 1));
        return new SKPoint(Math.Clamp(x, 0, Math.Max(0, imageWidth - 1)), Math.Clamp(y, 0, Math.Max(0, imageHeight - 1)));
    }

    private static SKPaint CreateTextPaint(float fontSize)
        => new()
        {
            Color = LabelTextColor,
            IsAntialias = true,
            TextSize = fontSize,
            Typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold),
        };

    private readonly record struct ImageMetrics(int Width, int Height, float FontSize);

    private readonly record struct BadgeStyle(SKColor? BackgroundColorOverride, float BorderWidth, float Radius);

    private enum CornerAnchor
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }
}