using AIDeskAssistant.Services;

namespace AIDeskAssistant.Tests;

public sealed class ScreenshotHistoryComparerTests
{
    [Fact]
    public void CalculateSimilarity_ReturnsOneForIdenticalImages()
    {
        byte[] imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WnRsl0AAAAASUVORK5CYII=");

        ScreenshotFingerprint left = ScreenshotHistoryComparer.CreateFingerprint(imageBytes);
        ScreenshotFingerprint right = ScreenshotHistoryComparer.CreateFingerprint(imageBytes);

        double similarity = ScreenshotHistoryComparer.CalculateSimilarity(left, right);

        Assert.Equal(1d, similarity);
    }

    [Fact]
    public void CalculateSimilarity_DetectsDifferentImages()
    {
        byte[] blackPixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WnRsl0AAAAASUVORK5CYII=");
        byte[] whitePixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

        ScreenshotFingerprint left = ScreenshotHistoryComparer.CreateFingerprint(blackPixel);
        ScreenshotFingerprint right = ScreenshotHistoryComparer.CreateFingerprint(whitePixel);

        double similarity = ScreenshotHistoryComparer.CalculateSimilarity(left, right);

        Assert.True(similarity < 0.5d);
    }
}