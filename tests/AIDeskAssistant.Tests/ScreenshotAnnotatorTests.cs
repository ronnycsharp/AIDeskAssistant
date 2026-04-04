using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using SkiaSharp;

namespace AIDeskAssistant.Tests;

public sealed class ScreenshotAnnotatorTests
{
    [Fact]
    public void Annotate_AddsVisibleOverlayToImage()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(sourceBytes, new ScreenshotAnnotationData(new WindowBounds(100, 200, 400, 300), 220, 320));

        Assert.NotEqual(sourceBytes, annotatedBytes);

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        SKColor topLeftPixel = annotatedBitmap.GetPixel(18, 18);
        Assert.NotEqual(SKColors.White, topLeftPixel);
    }

    [Fact]
    public void Annotate_WithSuggestedContentArea_DrawsOverlayInsideImage()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] sourceBytes = png.ToArray();

        WindowBounds contentArea = ScreenshotAnnotationData.CreateSuggestedContentArea(new WindowBounds(100, 200, 400, 300));
        byte[] annotatedBytes = ScreenshotAnnotator.Annotate(sourceBytes, new ScreenshotAnnotationData(new WindowBounds(100, 200, 400, 300), 220, 320, contentArea));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        SKColor contentPixel = annotatedBitmap.GetPixel(24, 56);
        Assert.NotEqual(SKColors.White, contentPixel);
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
                IntendedClickTarget: new ScreenshotClickTarget(280, 360, "Button")));

        using SKBitmap? annotatedBitmap = SKBitmap.Decode(annotatedBytes);
        Assert.NotNull(annotatedBitmap);

        SKColor contentPixel = annotatedBitmap.GetPixel(180, 160);
        Assert.NotEqual(SKColors.White, contentPixel);
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

        bool verticalGridVisible = Enumerable.Range(0, detailBitmap.Height)
            .Any(y => detailBitmap.GetPixel(100, y) != SKColors.White);
        bool horizontalGridVisible = Enumerable.Range(0, detailBitmap.Width)
            .Any(x => detailBitmap.GetPixel(x, 100) != SKColors.White);

        Assert.True(verticalGridVisible);
        Assert.True(horizontalGridVisible);
    }
}