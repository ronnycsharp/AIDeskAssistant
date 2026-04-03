using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics.CodeAnalysis;

namespace AIDeskAssistant.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "System.Drawing is intentionally enabled for this desktop app, including Unix support via runtime configuration.")]
internal static class ScreenshotAnnotator
{
    private static readonly Color CornerColor = Color.FromArgb(255, 0, 170, 140);
    private static readonly Color CursorColor = Color.FromArgb(255, 255, 106, 0);
    private static readonly Color RulerTickColor = Color.FromArgb(120, 255, 255, 255);
    private static readonly Color RulerGuideColor = Color.FromArgb(28, 255, 255, 255);
    private static readonly Color LabelBackgroundColor = Color.FromArgb(210, 18, 18, 18);
    private static readonly Color LabelBorderColor = Color.FromArgb(235, 255, 255, 255);
    private static readonly Brush LabelTextBrush = Brushes.White;

    public static byte[] Annotate(byte[] screenshotBytes, ScreenshotAnnotationData annotation)
    {
        try
        {
            using var input = new MemoryStream(screenshotBytes);
            using var image = Image.FromStream(input);
            using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, 0, 0, image.Width, image.Height);

            using var font = CreateFont(bitmap.Size);
            DrawEdgeRuler(graphics, font, bitmap.Size, annotation);
            DrawCornerAnnotations(graphics, font, bitmap.Size, annotation);
            DrawCursorAnnotation(graphics, font, bitmap.Size, annotation);

            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch
        {
            return screenshotBytes;
        }
    }

    private static Font CreateFont(Size imageSize)
    {
        float size = Math.Clamp(Math.Min(imageSize.Width, imageSize.Height) / 36f, 12f, 24f);
        return new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static void DrawEdgeRuler(Graphics graphics, Font font, Size imageSize, ScreenshotAnnotationData annotation)
    {
        int majorStep = CalculateMajorStep(imageSize);
        int minorStep = Math.Max(majorStep / 2, 25);
        float edgeBand = Math.Clamp(Math.Min(imageSize.Width, imageSize.Height) / 28f, 18f, 32f);
        using var tickPen = new Pen(RulerTickColor, 1.5f);
        using var guidePen = new Pen(RulerGuideColor, 1f);

        DrawVerticalRuler(graphics, font, imageSize, annotation, minorStep, majorStep, edgeBand, tickPen, guidePen);
        DrawHorizontalRuler(graphics, font, imageSize, annotation, minorStep, majorStep, edgeBand, tickPen, guidePen);
    }

    private static void DrawVerticalRuler(Graphics graphics, Font font, Size imageSize, ScreenshotAnnotationData annotation, int minorStep, int majorStep, float edgeBand, Pen tickPen, Pen guidePen)
    {
        for (int x = 0; x < imageSize.Width; x += minorStep)
        {
            bool isMajor = x % majorStep == 0;
            float tickLength = isMajor ? edgeBand : edgeBand * 0.45f;
            graphics.DrawLine(tickPen, x, 0, x, tickLength);
            graphics.DrawLine(tickPen, x, imageSize.Height - tickLength, x, imageSize.Height);

            if (!isMajor)
                continue;

            if (x > 0 && x < imageSize.Width - 1)
                graphics.DrawLine(guidePen, x, 0, x, imageSize.Height - 1);

            int globalX = annotation.CaptureBounds.X + x;
            DrawRulerLabel(graphics, font, globalX.ToString(), new PointF(Math.Min(imageSize.Width - 8f, x + 4f), 6f));
        }
    }

    private static void DrawHorizontalRuler(Graphics graphics, Font font, Size imageSize, ScreenshotAnnotationData annotation, int minorStep, int majorStep, float edgeBand, Pen tickPen, Pen guidePen)
    {
        for (int y = 0; y < imageSize.Height; y += minorStep)
        {
            bool isMajor = y % majorStep == 0;
            float tickLength = isMajor ? edgeBand : edgeBand * 0.45f;
            graphics.DrawLine(tickPen, 0, y, tickLength, y);
            graphics.DrawLine(tickPen, imageSize.Width - tickLength, y, imageSize.Width, y);

            if (!isMajor)
                continue;

            if (y > 0 && y < imageSize.Height - 1)
                graphics.DrawLine(guidePen, 0, y, imageSize.Width - 1, y);

            int globalY = annotation.CaptureBounds.Y + y;
            DrawRulerLabel(graphics, font, globalY.ToString(), new PointF(6f, Math.Min(imageSize.Height - 8f, y + 4f)));
        }
    }

    private static void DrawCornerAnnotations(Graphics graphics, Font font, Size imageSize, ScreenshotAnnotationData annotation)
    {
        var corners = new[]
        {
            new CornerLabel("TL", annotation.TopLeft, new Point(18, 18), CornerAnchor.TopLeft),
            new CornerLabel("TR", annotation.TopRight, new Point(imageSize.Width - 18, 18), CornerAnchor.TopRight),
            new CornerLabel("BL", annotation.BottomLeft, new Point(18, imageSize.Height - 18), CornerAnchor.BottomLeft),
            new CornerLabel("BR", annotation.BottomRight, new Point(imageSize.Width - 18, imageSize.Height - 18), CornerAnchor.BottomRight),
        };

        foreach (CornerLabel corner in corners)
        {
            Point anchorPoint = GetCornerAnchorPoint(imageSize, corner.Anchor);
            DrawLabelWithAnchor(
                graphics,
                font,
                $"{corner.Name} ({corner.GlobalPoint.X},{corner.GlobalPoint.Y})",
                corner.LabelOrigin,
                anchorPoint,
                corner.Anchor,
                CornerColor);
        }
    }

    private static void DrawCursorAnnotation(Graphics graphics, Font font, Size imageSize, ScreenshotAnnotationData annotation)
    {
        string cursorText = $"Cursor ({annotation.CursorX},{annotation.CursorY})";
        if (!annotation.CursorIsInsideCapture)
        {
            DrawDetachedBadge(
                graphics,
                font,
                $"{cursorText} outside capture",
                new RectangleF(18, 18, Math.Max(180, imageSize.Width - 36), 36),
                CursorColor);
            return;
        }

        Point cursorPoint = MapGlobalPointToImage(annotation.CaptureBounds, annotation.CursorX, annotation.CursorY, imageSize);
        float radius = Math.Clamp(Math.Min(imageSize.Width, imageSize.Height) / 24f, 16f, 34f);
        using var fillBrush = new SolidBrush(Color.FromArgb(80, CursorColor));
        using var pen = new Pen(CursorColor, Math.Max(3f, radius / 6f));

        graphics.FillEllipse(fillBrush, cursorPoint.X - radius, cursorPoint.Y - radius, radius * 2, radius * 2);
        graphics.DrawEllipse(pen, cursorPoint.X - radius, cursorPoint.Y - radius, radius * 2, radius * 2);
        graphics.DrawLine(pen, cursorPoint.X - radius - 10, cursorPoint.Y, cursorPoint.X + radius + 10, cursorPoint.Y);
        graphics.DrawLine(pen, cursorPoint.X, cursorPoint.Y - radius - 10, cursorPoint.X, cursorPoint.Y + radius + 10);

        Point labelOrigin = new(
            Math.Min(imageSize.Width - 18, cursorPoint.X + (int)radius + 14),
            Math.Max(18, cursorPoint.Y - (int)radius - 10));

        DrawLabelWithAnchor(graphics, font, cursorText, labelOrigin, cursorPoint, CornerAnchor.TopLeft, CursorColor);
    }

    private static void DrawDetachedBadge(Graphics graphics, Font font, string text, RectangleF preferredBounds, Color accentColor)
    {
        SizeF textSize = graphics.MeasureString(text, font);
        float width = Math.Min(preferredBounds.Width, textSize.Width + 20f);
        RectangleF badgeRect = new(preferredBounds.X, preferredBounds.Y, width, textSize.Height + 14f);
        DrawBadge(graphics, font, text, badgeRect, accentColor);
    }

    private static void DrawRulerLabel(Graphics graphics, Font font, string text, PointF origin)
    {
        using var labelFont = new Font(font.FontFamily, Math.Max(10f, font.Size * 0.7f), FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF textSize = graphics.MeasureString(text, labelFont);
        RectangleF rect = ClampRectangle(new RectangleF(origin.X, origin.Y, textSize.Width + 10f, textSize.Height + 6f), graphics.VisibleClipBounds.Size);
        using var backgroundBrush = new SolidBrush(Color.FromArgb(105, 10, 10, 10));
        using var borderPen = new Pen(Color.FromArgb(95, 255, 255, 255), 1f);
        using GraphicsPath path = CreateRoundedRectangle(rect, 6f);

        graphics.FillPath(backgroundBrush, path);
        graphics.DrawPath(borderPen, path);

        RectangleF textRect = new(rect.X + 5f, rect.Y + 3f, rect.Width - 10f, rect.Height - 6f);
        using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        graphics.DrawString(text, labelFont, LabelTextBrush, textRect, format);
    }

    private static void DrawLabelWithAnchor(
        Graphics graphics,
        Font font,
        string text,
        Point preferredOrigin,
        Point anchorPoint,
        CornerAnchor anchor,
        Color accentColor)
    {
        SizeF textSize = graphics.MeasureString(text, font);
        float width = textSize.Width + 20f;
        float height = textSize.Height + 14f;

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

        RectangleF rect = new(x, y, width, height);
        rect = ClampRectangle(rect, graphics.VisibleClipBounds.Size);

        using var connectorPen = new Pen(accentColor, 3f);
        Point rectAnchor = GetNearestPointOnRectangle(rect, anchorPoint);
        graphics.DrawLine(connectorPen, anchorPoint, rectAnchor);
        DrawBadge(graphics, font, text, rect, accentColor);
    }

    private static void DrawBadge(Graphics graphics, Font font, string text, RectangleF rect, Color accentColor)
    {
        using var backgroundBrush = new SolidBrush(LabelBackgroundColor);
        using var borderPen = new Pen(accentColor, 2f);
        using var innerBorderPen = new Pen(LabelBorderColor, 1f);
        using GraphicsPath path = CreateRoundedRectangle(rect, 10f);

        graphics.FillPath(backgroundBrush, path);
        graphics.DrawPath(borderPen, path);
        graphics.DrawPath(innerBorderPen, path);

        RectangleF textRect = new(rect.X + 10f, rect.Y + 7f, rect.Width - 20f, rect.Height - 14f);
        using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        graphics.DrawString(text, font, LabelTextBrush, textRect, format);
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
    {
        float diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static RectangleF ClampRectangle(RectangleF rect, SizeF bounds)
    {
        float x = Math.Clamp(rect.X, 8f, Math.Max(8f, bounds.Width - rect.Width - 8f));
        float y = Math.Clamp(rect.Y, 8f, Math.Max(8f, bounds.Height - rect.Height - 8f));
        return new RectangleF(x, y, rect.Width, rect.Height);
    }

    private static Point GetNearestPointOnRectangle(RectangleF rect, Point point)
    {
        int x = (int)Math.Clamp(point.X, rect.Left, rect.Right);
        int y = (int)Math.Clamp(point.Y, rect.Top, rect.Bottom);
        return new Point(x, y);
    }

    private static Point GetCornerAnchorPoint(Size imageSize, CornerAnchor anchor)
    {
        return anchor switch
        {
            CornerAnchor.TopLeft => new Point(0, 0),
            CornerAnchor.TopRight => new Point(Math.Max(0, imageSize.Width - 1), 0),
            CornerAnchor.BottomLeft => new Point(0, Math.Max(0, imageSize.Height - 1)),
            CornerAnchor.BottomRight => new Point(Math.Max(0, imageSize.Width - 1), Math.Max(0, imageSize.Height - 1)),
            _ => Point.Empty,
        };
    }

    private static Point MapGlobalPointToImage(AIDeskAssistant.Models.WindowBounds bounds, int globalX, int globalY, Size imageSize)
    {
        double relativeX = bounds.Width <= 1 ? 0d : (double)(globalX - bounds.X) / (bounds.Width - 1);
        double relativeY = bounds.Height <= 1 ? 0d : (double)(globalY - bounds.Y) / (bounds.Height - 1);

        int x = (int)Math.Round(relativeX * Math.Max(0, imageSize.Width - 1));
        int y = (int)Math.Round(relativeY * Math.Max(0, imageSize.Height - 1));
        return new Point(Math.Clamp(x, 0, Math.Max(0, imageSize.Width - 1)), Math.Clamp(y, 0, Math.Max(0, imageSize.Height - 1)));
    }

    private static int CalculateMajorStep(Size imageSize)
    {
        int reference = Math.Min(imageSize.Width, imageSize.Height);
        if (reference >= 1800)
            return 200;

        if (reference >= 900)
            return 100;

        return 50;
    }

    private readonly record struct CornerLabel(string Name, (int X, int Y) GlobalPoint, Point LabelOrigin, CornerAnchor Anchor);

    private enum CornerAnchor
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }
}