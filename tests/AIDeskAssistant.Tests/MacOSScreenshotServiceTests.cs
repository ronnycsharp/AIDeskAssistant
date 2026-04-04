using AIDeskAssistant.Models;
using AIDeskAssistant.Platform.MacOS;
using AIDeskAssistant.Services;
using SkiaSharp;

namespace AIDeskAssistant.Tests;

public sealed class MacOSScreenshotServiceTests
{
    [Fact]
    public void BuildScreenCaptureKitArguments_IncludesOverlayExclusionsAndFocusPoint()
    {
        string[] arguments = MacOSScreenshotService.BuildScreenCaptureKitArguments(
            new ScreenshotCaptureOptions(new WindowBounds(100, 200, 300, 120)),
            "/tmp/helper.swift",
            "/tmp/capture.png");

        Assert.Equal("swift", arguments[0]);
        Assert.Contains("--exclude-title", arguments);
        Assert.Contains(MacOSScreenshotService.OverlayWindowTitle, arguments);
        Assert.Contains("--point-x", arguments);
        Assert.Contains("250", arguments);
        Assert.Contains("--point-y", arguments);
        Assert.Contains("260", arguments);
    }

    [Fact]
    public void CropDisplayCaptureToBounds_ReturnsRequestedSubset()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        surface.Canvas.Clear(SKColors.White);
        using var fillPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
        surface.Canvas.DrawRect(new SKRect(50, 50, 150, 110), fillPaint);
        using SKImage image = surface.Snapshot();
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);

        byte[] croppedBytes = MacOSScreenshotService.CropDisplayCaptureToBounds(
            png.ToArray(),
            new WindowBounds(100, 200, 400, 300),
            new WindowBounds(150, 250, 100, 60));

        using SKBitmap? croppedBitmap = SKBitmap.Decode(croppedBytes);
        Assert.NotNull(croppedBitmap);
        Assert.Equal(100, croppedBitmap.Width);
        Assert.Equal(60, croppedBitmap.Height);
        Assert.Equal(SKColors.Red, croppedBitmap.GetPixel(10, 10));
    }
}