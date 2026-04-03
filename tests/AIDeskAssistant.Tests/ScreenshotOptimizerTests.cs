using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class ScreenshotOptimizerTests
{
    [Fact]
    public void ReadFromEnvironment_UsesFullQualityByDefault()
    {
        string? original = Environment.GetEnvironmentVariable("AIDESK_SCREENSHOT_JPEG_QUALITY");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_SCREENSHOT_JPEG_QUALITY", null);

            ScreenshotOptimizationOptions options = ScreenshotOptimizer.ReadFromEnvironment();

            Assert.Equal(100, options.JpegQuality);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_SCREENSHOT_JPEG_QUALITY", original);
        }
    }

    [Fact]
    public void ReadFromEnvironment_ClampsQualityToOneHundred()
    {
        string? original = Environment.GetEnvironmentVariable("AIDESK_SCREENSHOT_JPEG_QUALITY");

        try
        {
            Environment.SetEnvironmentVariable("AIDESK_SCREENSHOT_JPEG_QUALITY", "200");

            ScreenshotOptimizationOptions options = ScreenshotOptimizer.ReadFromEnvironment();

            Assert.Equal(100, options.JpegQuality);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIDESK_SCREENSHOT_JPEG_QUALITY", original);
        }
    }
}