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

        SKColor contentPixel = annotatedBitmap.GetPixel(180, 160);
        Assert.NotEqual(SKColors.White, contentPixel);
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

        SKColor framePixel = annotatedBitmap.GetPixel(150, 80);
        Assert.NotEqual(SKColors.White, framePixel);
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
        SKColor targetPixel = annotatedBitmap.GetPixel(170, 105);

        static int ColorDistance(SKColor left, SKColor right)
            => Math.Abs(left.Red - right.Red)
                + Math.Abs(left.Green - right.Green)
                + Math.Abs(left.Blue - right.Blue);

        int backgroundDistance = ColorDistance(backgroundPixel, originalColor);
        int targetDistance = ColorDistance(targetPixel, originalColor);
        int bestTargetDistance = Enumerable.Range(160, 50)
            .SelectMany(x => Enumerable.Range(90, 30).Select(y => ColorDistance(annotatedBitmap.GetPixel(x, y), originalColor)))
            .Min();

        Assert.True(backgroundDistance > 40);
        Assert.True(targetDistance < backgroundDistance || bestTargetDistance < backgroundDistance);
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
}