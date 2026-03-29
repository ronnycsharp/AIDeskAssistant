using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

public interface IScreenshotService
{
    /// <summary>Takes a full-screen screenshot and returns the image as a PNG byte array.</summary>
    byte[] TakeScreenshot();

    /// <summary>Returns information about the primary screen (width, height, bit depth).</summary>
    ScreenInfo GetScreenInfo();
}
