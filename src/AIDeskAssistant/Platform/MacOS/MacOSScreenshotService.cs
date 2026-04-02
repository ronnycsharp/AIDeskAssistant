using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSScreenshotService : IScreenshotService
{
    // CoreGraphics / ImageIO interop
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nuint CGDisplayPixelsWide(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nuint CGDisplayPixelsHigh(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nuint CGDisplayBitsPerPixel(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGDisplayCreateImage(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern bool CGImageDestinationFinalize(IntPtr destination);

    public byte[] TakeScreenshot(ScreenshotCaptureOptions options = default)
    {
        // Use the screencapture command-line tool (available on all macOS versions).
        // Use a filename without spaces/special chars to avoid argument-injection issues.
        string tmpFile = Path.Combine(
            Path.GetTempPath(),
            $"aideskassistant_{Guid.NewGuid():N}.png");
        try
        {
            string[] args = BuildScreencaptureArguments(options, tmpFile);
            var psi = new System.Diagnostics.ProcessStartInfo(
                "screencapture",
                args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(10_000);
            return File.ReadAllBytes(tmpFile);
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    private static string[] BuildScreencaptureArguments(ScreenshotCaptureOptions options, string tmpFile)
    {
        var args = new List<string> { "-x", "-C" };

        if (options.Bounds is WindowBounds bounds)
            args.Add($"-R{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");

        args.Add(tmpFile);
        return args.ToArray();
    }

    public ScreenInfo GetScreenInfo()
    {
        IntPtr display = CGMainDisplayID();
        int width  = (int)CGDisplayPixelsWide(display);
        int height = (int)CGDisplayPixelsHigh(display);
        int depth  = (int)CGDisplayBitsPerPixel(display);
        return new ScreenInfo(width, height, depth);
    }
}
