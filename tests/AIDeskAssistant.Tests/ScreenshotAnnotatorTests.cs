using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using SkiaSharp;

namespace AIDeskAssistant.Tests;

public sealed class ScreenshotAnnotatorTests
{
    [Fact]
    public void Annotate_StandardStyle_PreservesPlainImageWithoutCornerOverlays()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(sourceBytes, new ScreenshotAnnotationData(new WindowBounds(100, 200, 400, 300), 220, 320));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        SKColor topLeftPixel = annotatedBitmap.GetPixel(18, 18);
        Assert.Equal(SKColors.White, topLeftPixel);
    }

    [Fact]
    public void Annotate_WithSuggestedContentArea_DoesNotDrawBlueOutline()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        WindowBounds contentArea = ScreenshotAnnotationData.CreateSuggestedContentArea(new WindowBounds(100, 200, 400, 300));
        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(
            sourceBytes,
            new ScreenshotAnnotationData(
                new WindowBounds(100, 200, 400, 300),
                220,
                320,
                SuggestedContentArea: contentArea));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        SKColor formerOutlinePixel = annotatedBitmap.GetPixel(24, 56);
        bool looksLikeBlueOutline = formerOutlinePixel.Blue > formerOutlinePixel.Red + 20
            && formerOutlinePixel.Blue > formerOutlinePixel.Green + 20;
        Assert.False(looksLikeBlueOutline);

        SKColor centralPixel = annotatedBitmap.GetPixel(120, 120);
        Assert.Equal(SKColors.White, centralPixel);
    }

    [Fact]
    public void Annotate_WithIntendedClickTarget_DrawsMarkerNearTarget()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(
            sourceBytes,
            new ScreenshotAnnotationData(
                new WindowBounds(100, 200, 400, 300),
                220,
                320,
                ScreenshotVisualStyles.SchematicTarget,
                IntendedClickTarget: new ScreenshotClickTarget(280, 360, "Button")));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        bool hasRedHighlight = RegionContains(
            annotatedBitmap,
            150,
            130,
            100,
            80,
            static pixel => pixel.Red > pixel.Green + 20 && pixel.Red > pixel.Blue + 20);
        Assert.True(hasRedHighlight);
    }

    [Fact]
    public void Annotate_WithHighlightedAxElement_DrawsFramedRegion()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(
            sourceBytes,
            new ScreenshotAnnotationData(
                new WindowBounds(100, 200, 400, 300),
                220,
                320,
                ScreenshotVisualStyles.SchematicTarget,
                IntendedElementRegion: new ScreenshotHighlightedRegion(new WindowBounds(250, 280, 90, 50), "Save button")));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        bool hasRedOutline = RegionContains(
            annotatedBitmap,
            145,
            75,
            110,
            70,
            static pixel => pixel.Red > pixel.Green + 20 && pixel.Red > pixel.Blue + 20);
        Assert.True(hasRedOutline);
    }

    [Fact]
    public void Annotate_WithSchematicTargetStyle_EmphasizesHighlightedTarget()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(new SKColor(40, 160, 220));
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(
            sourceBytes,
            new ScreenshotAnnotationData(
                new WindowBounds(100, 200, 400, 300),
                220,
                320,
                ScreenshotVisualStyles.SchematicTarget,
                IntendedElementRegion: new ScreenshotHighlightedRegion(new WindowBounds(250, 280, 90, 50), "calculator_key=7")));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        SKColor originalColor = new(40, 160, 220);
        SKColor backgroundPixel = annotatedBitmap.GetPixel(20, 20);
        static int ColorDistance(SKColor left, SKColor right)
            => Math.Abs(left.Red - right.Red)
                + Math.Abs(left.Green - right.Green)
                + Math.Abs(left.Blue - right.Blue);

        int backgroundDistance = ColorDistance(backgroundPixel, originalColor);
        Assert.True(backgroundDistance > 0);
        int backgroundChannelSpread = Math.Max(backgroundPixel.Red, Math.Max(backgroundPixel.Green, backgroundPixel.Blue))
            - Math.Min(backgroundPixel.Red, Math.Min(backgroundPixel.Green, backgroundPixel.Blue));
        Assert.True(backgroundChannelSpread < 40);
    }

    [Fact]
    public void CursorDetailExtractor_DrawsCoordinateGrid()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        ScreenshotCursorDetail? detail = ScreenshotCursorDetailExtractor.Create(
            sourceBytes,
            new ScreenshotAnnotationData(new WindowBounds(100, 200, 400, 300), 220, 320));

        Assert.NotNull(detail);

        using SKBitmap? detailBitmap = SKBitmap.Decode(detail.Bytes);
        Assert.NotNull(detailBitmap);

        bool labelRegionVisible = Enumerable.Range(0, Math.Min(40, detailBitmap.Width))
            .Any(x => Enumerable.Range(0, Math.Min(30, detailBitmap.Height))
                .Any(y => detailBitmap.GetPixel(x, y) != SKColors.White));
        bool anyRasterVisible = Enumerable.Range(0, detailBitmap.Width)
            .Any(x => Enumerable.Range(0, detailBitmap.Height)
                .Any(y => detailBitmap.GetPixel(x, y) != SKColors.White));

        Assert.True(labelRegionVisible);
        Assert.True(anyRasterVisible);
    }

    private static bool RegionContains(SKBitmap bitmap, int startX, int startY, int width, int height, Func<SKColor, bool> predicate)
    {
        int maxX = Math.Min(bitmap.Width, startX + width);
        int maxY = Math.Min(bitmap.Height, startY + height);

        for (int x = Math.Max(0, startX); x < maxX; x++)
        {
            for (int y = Math.Max(0, startY); y < maxY; y++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                    return true;
            }
        }

        return false;
    }
}