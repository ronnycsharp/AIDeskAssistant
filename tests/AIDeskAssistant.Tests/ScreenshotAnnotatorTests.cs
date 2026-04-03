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

        SKColor contentPixel = annotatedBitmap.GetPixel(40, 60);
        Assert.NotEqual(SKColors.White, contentPixel);
    }
}