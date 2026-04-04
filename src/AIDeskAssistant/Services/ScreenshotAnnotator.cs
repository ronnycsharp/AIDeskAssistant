using AIDeskAssistant.Models;
using SkiaSharp;

namespace AIDeskAssistant.Services;

internal static class ScreenshotAnnotator
{
    private static readonly SKColor CornerColor = new(0, 170, 140, 255);
    private static readonly SKColor CursorColor = new(255, 106, 0, 255);
    private static readonly SKColor IntendedClickColor = new(255, 208, 0, 255);
    private static readonly SKColor IntendedElementColor = new(64, 255, 191, 255);
    private static readonly SKColor AxMarkColor = new(79, 195, 247, 255);
    private static readonly SKColor OcrMarkColor = new(255, 99, 132, 255);
    private static readonly SKColor RulerTickColor = new(255, 255, 255, 120);
    private static readonly SKColor RulerGuideColor = new(255, 255, 255, 28);
    private static readonly SKColor MinorGridColor = new(255, 255, 255, 18);
    private static readonly SKColor MajorGridColor = new(255, 255, 255, 34);
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
            canvas.DrawBitmap(sourceBitmap, 0, 0);

            float fontSize = CreateFontSize(sourceBitmap.Width, sourceBitmap.Height);
            var metrics = new ImageMetrics(sourceBitmap.Width, sourceBitmap.Height, fontSize);
            DrawCoordinateRaster(canvas, metrics, annotation);
            DrawNumberedMarks(canvas, metrics, annotation);
            DrawIntendedElementRegion(canvas, metrics, annotation);
            DrawIntendedClickTarget(canvas, metrics, annotation);
            DrawCornerAnnotations(canvas, metrics, annotation);
            DrawCursorAnnotation(canvas, metrics, annotation);

            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            return screenshotBytes;
        }
    }

    private static float CreateFontSize(int imageWidth, int imageHeight)
        => Math.Clamp(Math.Min(imageWidth, imageHeight) / 36f, 12f, 24f);

    private static void DrawCoordinateRaster(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        int majorStep = CalculateMajorStep(annotation.CaptureBounds.Width, annotation.CaptureBounds.Height);
        int minorStep = Math.Max(majorStep / 2, 25);
        float edgeBand = Math.Clamp(Math.Min(metrics.Width, metrics.Height) / 28f, 18f, 32f);
        using var tickPaint = new SKPaint { Color = RulerTickColor, IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        using var guidePaint = new SKPaint { Color = RulerGuideColor, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke };
        using var minorGridPaint = new SKPaint { Color = MinorGridColor, IsAntialias = true, StrokeWidth = 1f, Style = SKPaintStyle.Stroke };
        using var majorGridPaint = new SKPaint { Color = MajorGridColor, IsAntialias = true, StrokeWidth = 1.25f, Style = SKPaintStyle.Stroke };
        var rulerStyle = new RulerStyle(minorStep, majorStep, edgeBand, tickPaint, guidePaint, minorGridPaint, majorGridPaint);

        DrawGridLines(canvas, metrics, annotation, rulerStyle);

        DrawVerticalRuler(canvas, metrics, annotation, rulerStyle);
        DrawHorizontalRuler(canvas, metrics, annotation, rulerStyle);
    }

    private static void DrawGridLines(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation, RulerStyle rulerStyle)
    {
        int left = annotation.CaptureBounds.X;
        int right = annotation.CaptureBounds.X + Math.Max(0, annotation.CaptureBounds.Width - 1);
        int top = annotation.CaptureBounds.Y;
        int bottom = annotation.CaptureBounds.Y + Math.Max(0, annotation.CaptureBounds.Height - 1);

        for (int globalX = AlignDown(left, rulerStyle.MinorStep); globalX <= right; globalX += rulerStyle.MinorStep)
        {
            if (globalX < left)
                continue;

            float x = MapGlobalXToImage(annotation, globalX, metrics.Width);
            bool isMajor = globalX % rulerStyle.MajorStep == 0;
            canvas.DrawLine(x, 0, x, metrics.Height - 1, isMajor ? rulerStyle.MajorGridPaint : rulerStyle.MinorGridPaint);
        }

        for (int globalY = AlignDown(top, rulerStyle.MinorStep); globalY <= bottom; globalY += rulerStyle.MinorStep)
        {
            if (globalY < top)
                continue;

            float y = MapGlobalYToImage(annotation, globalY, metrics.Height);
            bool isMajor = globalY % rulerStyle.MajorStep == 0;
            canvas.DrawLine(0, y, metrics.Width - 1, y, isMajor ? rulerStyle.MajorGridPaint : rulerStyle.MinorGridPaint);
        }
    }

    private static void DrawVerticalRuler(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation, RulerStyle rulerStyle)
    {
        int left = annotation.CaptureBounds.X;
        int right = annotation.CaptureBounds.X + Math.Max(0, annotation.CaptureBounds.Width - 1);

        for (int globalX = AlignDown(left, rulerStyle.MinorStep); globalX <= right; globalX += rulerStyle.MinorStep)
        {
            if (globalX < left)
                continue;

            float x = MapGlobalXToImage(annotation, globalX, metrics.Width);
            bool isMajor = globalX % rulerStyle.MajorStep == 0;
            float tickLength = isMajor ? rulerStyle.EdgeBand : rulerStyle.EdgeBand * 0.45f;
            canvas.DrawLine(x, 0, x, tickLength, rulerStyle.TickPaint);
            canvas.DrawLine(x, metrics.Height - tickLength, x, metrics.Height, rulerStyle.TickPaint);

            if (!isMajor)
                continue;

            if (x > 0 && x < metrics.Width - 1)
                canvas.DrawLine(x, 0, x, metrics.Height - 1, rulerStyle.GuidePaint);

            DrawRulerLabel(canvas, metrics.FontSize, globalX.ToString(), new SKPoint(Math.Min(metrics.Width - 8f, x + 4f), 6f), metrics.Width, metrics.Height);
        }
    }

    private static void DrawHorizontalRuler(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation, RulerStyle rulerStyle)
    {
        int top = annotation.CaptureBounds.Y;
        int bottom = annotation.CaptureBounds.Y + Math.Max(0, annotation.CaptureBounds.Height - 1);

        for (int globalY = AlignDown(top, rulerStyle.MinorStep); globalY <= bottom; globalY += rulerStyle.MinorStep)
        {
            if (globalY < top)
                continue;

            float y = MapGlobalYToImage(annotation, globalY, metrics.Height);
            bool isMajor = globalY % rulerStyle.MajorStep == 0;
            float tickLength = isMajor ? rulerStyle.EdgeBand : rulerStyle.EdgeBand * 0.45f;
            canvas.DrawLine(0, y, tickLength, y, rulerStyle.TickPaint);
            canvas.DrawLine(metrics.Width - tickLength, y, metrics.Width, y, rulerStyle.TickPaint);

            if (!isMajor)
                continue;

            if (y > 0 && y < metrics.Height - 1)
                canvas.DrawLine(0, y, metrics.Width - 1, y, rulerStyle.GuidePaint);

            DrawRulerLabel(canvas, metrics.FontSize, globalY.ToString(), new SKPoint(6f, Math.Min(metrics.Height - 8f, y + 4f)), metrics.Width, metrics.Height);
        }
    }

    private static void DrawCornerAnnotations(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        var corners = new[]
        {
            new CornerLabel("TL", annotation.TopLeft, new SKPoint(18, 18), CornerAnchor.TopLeft),
            new CornerLabel("TR", annotation.TopRight, new SKPoint(metrics.Width - 18, 18), CornerAnchor.TopRight),
            new CornerLabel("BL", annotation.BottomLeft, new SKPoint(18, metrics.Height - 18), CornerAnchor.BottomLeft),
            new CornerLabel("BR", annotation.BottomRight, new SKPoint(metrics.Width - 18, metrics.Height - 18), CornerAnchor.BottomRight),
        };

        foreach (CornerLabel corner in corners)
        {
            SKPoint anchorPoint = GetCornerAnchorPoint(metrics.Width, metrics.Height, corner.Anchor);
            DrawLabelWithAnchor(
                canvas,
                metrics,
                $"{corner.Name} ({corner.GlobalPoint.X},{corner.GlobalPoint.Y})",
                corner.LabelOrigin,
                anchorPoint,
                corner.Anchor,
                CornerColor);
        }
    }

    private static void DrawIntendedClickTarget(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        if (!annotation.HasIntendedClickTarget || annotation.IntendedClickTarget is not ScreenshotClickTarget clickTarget)
            return;

        string labelText = string.IsNullOrWhiteSpace(clickTarget.Label)
            ? $"Intended click ({clickTarget.X},{clickTarget.Y})"
            : $"{clickTarget.Label} ({clickTarget.X},{clickTarget.Y})";

        if (!annotation.IntendedClickIsInsideCapture)
        {
            DrawDetachedBadge(
                canvas,
                metrics.FontSize,
                $"{labelText} outside capture",
                new SKRect(18, 58, Math.Max(220, metrics.Width - 18), 98),
                IntendedClickColor,
                metrics.Width,
                metrics.Height);
            return;
        }

        SKPoint targetPoint = MapGlobalPointToImage(annotation, clickTarget.X, clickTarget.Y, metrics.Width, metrics.Height);
        float outerRadius = Math.Clamp(Math.Min(metrics.Width, metrics.Height) / 22f, 18f, 36f);
        float innerRadius = Math.Max(6f, outerRadius * 0.42f);
        float guideLength = outerRadius * 0.9f;
        float guideGap = Math.Max(5f, outerRadius * 0.35f);
        using var fillPaint = new SKPaint { Color = IntendedClickColor.WithAlpha(44), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint { Color = IntendedClickColor, IsAntialias = true, StrokeWidth = Math.Max(3f, outerRadius / 7f), Style = SKPaintStyle.Stroke };
        using var guidePaint = new SKPaint { Color = IntendedClickColor.WithAlpha(235), IsAntialias = true, StrokeWidth = Math.Max(3f, outerRadius / 8f), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

        canvas.DrawCircle(targetPoint, outerRadius, fillPaint);
        canvas.DrawCircle(targetPoint, outerRadius, strokePaint);
        canvas.DrawCircle(targetPoint, innerRadius, strokePaint);
        canvas.DrawLine(targetPoint.X - outerRadius - guideLength, targetPoint.Y, targetPoint.X - guideGap, targetPoint.Y, guidePaint);
        canvas.DrawLine(targetPoint.X + guideGap, targetPoint.Y, targetPoint.X + outerRadius + guideLength, targetPoint.Y, guidePaint);
        canvas.DrawLine(targetPoint.X, targetPoint.Y - outerRadius - guideLength, targetPoint.X, targetPoint.Y - guideGap, guidePaint);
        canvas.DrawLine(targetPoint.X, targetPoint.Y + guideGap, targetPoint.X, targetPoint.Y + outerRadius + guideLength, guidePaint);

        SKPoint labelOrigin = new(
            Math.Min(metrics.Width - 18, targetPoint.X + outerRadius + 18),
            Math.Min(metrics.Height - 18, targetPoint.Y + outerRadius + 12));

        DrawLabelWithAnchor(canvas, metrics, labelText, labelOrigin, targetPoint, CornerAnchor.TopLeft, IntendedClickColor);
    }

    private static void DrawIntendedElementRegion(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        if (!annotation.HasIntendedElementRegion || annotation.IntendedElementRegion is not ScreenshotHighlightedRegion region)
            return;

        string labelText = string.IsNullOrWhiteSpace(region.Label)
            ? $"AX element ({region.Bounds.X},{region.Bounds.Y},{region.Bounds.Width}x{region.Bounds.Height})"
            : $"{region.Label} ({region.Bounds.X},{region.Bounds.Y},{region.Bounds.Width}x{region.Bounds.Height})";

        if (!annotation.IntendedElementRegionIntersectsCapture)
        {
            DrawDetachedBadge(
                canvas,
                metrics.FontSize,
                $"{labelText} outside capture",
                new SKRect(18, 102, Math.Max(240, metrics.Width - 18), 144),
                IntendedElementColor,
                metrics.Width,
                metrics.Height);
            return;
        }

        int right = region.Bounds.X + Math.Max(0, region.Bounds.Width - 1);
        int bottom = region.Bounds.Y + Math.Max(0, region.Bounds.Height - 1);
        SKPoint topLeft = MapGlobalPointToImage(annotation, region.Bounds.X, region.Bounds.Y, metrics.Width, metrics.Height);
        SKPoint bottomRight = MapGlobalPointToImage(annotation, right, bottom, metrics.Width, metrics.Height);
        SKRect rect = NormalizeRect(new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y));

        using var outerPaint = new SKPaint { Color = IntendedElementColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f, PathEffect = SKPathEffect.CreateDash(new float[] { 18f, 10f }, 0f) };
        using var innerPaint = new SKPaint { Color = LabelBorderColor.WithAlpha(220), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

        canvas.DrawRect(rect, outerPaint);
        canvas.DrawRect(new SKRect(rect.Left + 3f, rect.Top + 3f, rect.Right - 3f, rect.Bottom - 3f), innerPaint);

        SKPoint anchorPoint = new(rect.MidX, rect.Top);
        SKPoint labelOrigin = new(Math.Min(metrics.Width - 18, rect.Left + 12f), Math.Max(18f, rect.Top + 12f));
        DrawLabelWithAnchor(canvas, metrics, labelText, labelOrigin, anchorPoint, CornerAnchor.TopLeft, IntendedElementColor);
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

    private static void DrawCursorAnnotation(SKCanvas canvas, ImageMetrics metrics, ScreenshotAnnotationData annotation)
    {
        string cursorText = $"Cursor ({annotation.CursorX},{annotation.CursorY})";
        if (!annotation.CursorIsInsideCapture)
        {
            DrawDetachedBadge(
                canvas,
                metrics.FontSize,
                $"{cursorText} outside capture",
                new SKRect(18, 18, Math.Max(180, metrics.Width - 18), 54),
                CursorColor,
                metrics.Width,
                metrics.Height);
            return;
        }

        SKPoint cursorPoint = MapGlobalPointToImage(annotation, annotation.CursorX, annotation.CursorY, metrics.Width, metrics.Height);
        float radius = Math.Clamp(Math.Min(metrics.Width, metrics.Height) / 24f, 16f, 34f);
        using var fillPaint = new SKPaint { Color = CursorColor.WithAlpha(80), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var strokePaint = new SKPaint { Color = CursorColor, IsAntialias = true, StrokeWidth = Math.Max(3f, radius / 6f), Style = SKPaintStyle.Stroke };

        canvas.DrawCircle(cursorPoint, radius, fillPaint);
        canvas.DrawCircle(cursorPoint, radius, strokePaint);

        SKPoint labelOrigin = new(
            Math.Min(metrics.Width - 18, cursorPoint.X + radius + 14),
            Math.Max(18, cursorPoint.Y - radius - 10));

        DrawLabelWithAnchor(canvas, metrics, cursorText, labelOrigin, cursorPoint, CornerAnchor.TopLeft, CursorColor);
    }

    private static void DrawDetachedBadge(SKCanvas canvas, float fontSize, string text, SKRect preferredBounds, SKColor accentColor, int imageWidth, int imageHeight)
    {
        using var textPaint = CreateTextPaint(fontSize);
        SKRect textBounds = default;
        textPaint.MeasureText(text, ref textBounds);
        float width = Math.Min(preferredBounds.Width, textBounds.Width + 20f);
        SKRect badgeRect = new(preferredBounds.Left, preferredBounds.Top, preferredBounds.Left + width, preferredBounds.Top + textBounds.Height + 18f);
        DrawBadge(canvas, textPaint, text, badgeRect, accentColor, new ImageMetrics(imageWidth, imageHeight, fontSize));
    }

    private static void DrawRulerLabel(SKCanvas canvas, float fontSize, string text, SKPoint origin, int imageWidth, int imageHeight)
    {
        using var textPaint = CreateTextPaint(Math.Max(10f, fontSize * 0.7f));
        DrawBadge(
            canvas,
            textPaint,
            text,
            MeasureBadgeRect(textPaint, text, origin),
            LabelBorderColor.WithAlpha(95),
            new ImageMetrics(imageWidth, imageHeight, fontSize),
            new BadgeStyle(LabelBackgroundColor.WithAlpha(105), 1f, 6f));
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

    private static SKPoint GetCornerAnchorPoint(int imageWidth, int imageHeight, CornerAnchor anchor)
    {
        return anchor switch
        {
            CornerAnchor.TopLeft => new SKPoint(0, 0),
            CornerAnchor.TopRight => new SKPoint(Math.Max(0, imageWidth - 1), 0),
            CornerAnchor.BottomLeft => new SKPoint(0, Math.Max(0, imageHeight - 1)),
            CornerAnchor.BottomRight => new SKPoint(Math.Max(0, imageWidth - 1), Math.Max(0, imageHeight - 1)),
            _ => SKPoint.Empty,
        };
    }

    private static SKPoint MapGlobalPointToImage(ScreenshotAnnotationData annotation, int globalX, int globalY, int imageWidth, int imageHeight)
    {
        double relativeX = annotation.CaptureBounds.Width <= 1 ? 0d : (double)(globalX - annotation.CaptureBounds.X) / (annotation.CaptureBounds.Width - 1);
        double relativeY = annotation.CaptureBounds.Height <= 1 ? 0d : (double)(globalY - annotation.CaptureBounds.Y) / (annotation.CaptureBounds.Height - 1);

        float x = (float)Math.Round(relativeX * Math.Max(0, imageWidth - 1));
        float y = (float)Math.Round(relativeY * Math.Max(0, imageHeight - 1));
        return new SKPoint(Math.Clamp(x, 0, Math.Max(0, imageWidth - 1)), Math.Clamp(y, 0, Math.Max(0, imageHeight - 1)));
    }

    private static float MapGlobalXToImage(ScreenshotAnnotationData annotation, int globalX, int imageWidth)
        => MapGlobalPointToImage(annotation, globalX, annotation.CaptureBounds.Y, imageWidth, 1).X;

    private static float MapGlobalYToImage(ScreenshotAnnotationData annotation, int globalY, int imageHeight)
        => MapGlobalPointToImage(annotation, annotation.CaptureBounds.X, globalY, 1, imageHeight).Y;

    private static SKPaint CreateTextPaint(float fontSize)
        => new()
        {
            Color = LabelTextColor,
            IsAntialias = true,
            TextSize = fontSize,
            Typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold),
        };

    private static SKRect MeasureBadgeRect(SKPaint textPaint, string text, SKPoint origin)
    {
        SKRect textBounds = default;
        textPaint.MeasureText(text, ref textBounds);
        return new SKRect(origin.X, origin.Y, origin.X + textBounds.Width + 10f, origin.Y + textBounds.Height + 8f);
    }

    private static int CalculateMajorStep(int logicalWidth, int logicalHeight)
    {
        int reference = Math.Min(logicalWidth, logicalHeight);
        if (reference >= 1800)
            return 200;

        if (reference >= 900)
            return 100;

        return 50;
    }

    private static int AlignDown(int value, int step)
        => step <= 0 ? value : value - MathHelpers.PositiveModulo(value, step);

    private readonly record struct CornerLabel(string Name, (int X, int Y) GlobalPoint, SKPoint LabelOrigin, CornerAnchor Anchor);

    private readonly record struct ImageMetrics(int Width, int Height, float FontSize);

    private readonly record struct RulerStyle(int MinorStep, int MajorStep, float EdgeBand, SKPaint TickPaint, SKPaint GuidePaint, SKPaint MinorGridPaint, SKPaint MajorGridPaint);

    private readonly record struct BadgeStyle(SKColor? BackgroundColorOverride, float BorderWidth, float Radius);

    private enum CornerAnchor
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }
}

internal static class MathHelpers
{
    public static int PositiveModulo(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}