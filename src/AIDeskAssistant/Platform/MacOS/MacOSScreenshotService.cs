using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;
using SkiaSharp;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSScreenshotService : IScreenshotService
{
    internal const string OverlayWindowTitle = "AIDeskAssistantOverlayPanel";
    private const string LegacyOverlayWindowTitle = "AIDesk";
    private const string ScreenCaptureKitScriptFileName = "AIDeskAssistantScreenCaptureKit.swift";

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
        if (TryTakeScreenCaptureKitScreenshot(options, out byte[]? screenshotBytes))
            return screenshotBytes;

        return TakeScreenshotWithScreencapture(options);
    }

    private static byte[] TakeScreenshotWithScreencapture(ScreenshotCaptureOptions options)
    {
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

    private static bool TryTakeScreenCaptureKitScreenshot(ScreenshotCaptureOptions options, out byte[] screenshotBytes)
    {
        screenshotBytes = Array.Empty<byte>();

        if (!OperatingSystem.IsMacOSVersionAtLeast(13))
            return false;

        if (!TryResolveScreenCaptureKitScriptPath(out string scriptPath))
            return false;

        string outputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant_sck_{Guid.NewGuid():N}.png");
        try
        {
            ProcessStartInfo startInfo = new("xcrun")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string argument in BuildScreenCaptureKitArguments(options, scriptPath, outputPath))
                startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch the ScreenCaptureKit helper.");

            string standardOutput = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The helper already exited between timeout handling and Kill.
                }
                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
                return false;

            byte[] capturedBytes = File.ReadAllBytes(outputPath);
            if (options.Bounds is WindowBounds requestedBounds
                && TryParseScreenCaptureKitMetadata(standardOutput, out ScreenCaptureKitMetadata? metadata)
                && metadata is not null)
            {
                capturedBytes = CropDisplayCaptureToBounds(
                    capturedBytes,
                    new WindowBounds(metadata.DisplayX, metadata.DisplayY, metadata.DisplayWidth, metadata.DisplayHeight),
                    requestedBounds);
            }

            screenshotBytes = capturedBytes;
            return screenshotBytes.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
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

    internal static string[] BuildScreenCaptureKitArguments(ScreenshotCaptureOptions options, string scriptPath, string outputPath)
    {
        var args = new List<string>
        {
            "swift",
            scriptPath,
            "--output",
            outputPath,
            "--exclude-title",
            OverlayWindowTitle,
            "--exclude-title",
            LegacyOverlayWindowTitle,
        };

        if (options.Bounds is WindowBounds bounds)
        {
            args.Add("--point-x");
            args.Add((bounds.X + (bounds.Width / 2)).ToString());
            args.Add("--point-y");
            args.Add((bounds.Y + (bounds.Height / 2)).ToString());
        }

        return args.ToArray();
    }

    private static bool TryResolveScreenCaptureKitScriptPath(out string scriptPath)
    {
        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, ScreenCaptureKitScriptFileName),
            Path.Combine(AppContext.BaseDirectory, "Platform", "MacOS", ScreenCaptureKitScriptFileName),
        ];

        string? existingPath = candidatePaths.FirstOrDefault(File.Exists);
        if (existingPath is not null)
        {
            scriptPath = existingPath;
            return true;
        }

        scriptPath = string.Empty;
        return false;
    }

    private static bool TryParseScreenCaptureKitMetadata(string standardOutput, out ScreenCaptureKitMetadata? metadata)
    {
        metadata = null;

        if (string.IsNullOrWhiteSpace(standardOutput))
            return false;

        string jsonLine = standardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(static line => line.TrimStart().StartsWith('{'))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(jsonLine))
            return false;

        metadata = JsonSerializer.Deserialize<ScreenCaptureKitMetadata>(jsonLine);
        return metadata is not null;
    }

    internal static byte[] CropDisplayCaptureToBounds(byte[] screenshotBytes, WindowBounds displayBounds, WindowBounds requestedBounds)
    {
        using SKBitmap? sourceBitmap = SKBitmap.Decode(screenshotBytes);
        if (sourceBitmap is null)
            return screenshotBytes;

        WindowBounds cropBounds = IntersectBounds(displayBounds, requestedBounds);
        if (cropBounds.Width <= 0 || cropBounds.Height <= 0)
            return screenshotBytes;

        int cropX = cropBounds.X - displayBounds.X;
        int cropY = cropBounds.Y - displayBounds.Y;
        if (cropX == 0 && cropY == 0 && cropBounds.Width == sourceBitmap.Width && cropBounds.Height == sourceBitmap.Height)
            return screenshotBytes;

        var subset = new SKRectI(cropX, cropY, cropX + cropBounds.Width, cropY + cropBounds.Height);
        using SKBitmap croppedBitmap = new(cropBounds.Width, cropBounds.Height, sourceBitmap.ColorType, sourceBitmap.AlphaType);
        if (!sourceBitmap.ExtractSubset(croppedBitmap, subset))
            return screenshotBytes;

        using SKImage croppedImage = SKImage.FromBitmap(croppedBitmap);
        using SKData data = croppedImage.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static WindowBounds IntersectBounds(WindowBounds displayBounds, WindowBounds requestedBounds)
    {
        int left = Math.Max(displayBounds.X, requestedBounds.X);
        int top = Math.Max(displayBounds.Y, requestedBounds.Y);
        int right = Math.Min(displayBounds.X + displayBounds.Width, requestedBounds.X + requestedBounds.Width);
        int bottom = Math.Min(displayBounds.Y + displayBounds.Height, requestedBounds.Y + requestedBounds.Height);
        return new WindowBounds(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public ScreenInfo GetScreenInfo()
    {
        IntPtr display = CGMainDisplayID();
        int width  = (int)CGDisplayPixelsWide(display);
        int height = (int)CGDisplayPixelsHigh(display);
        int depth  = (int)CGDisplayBitsPerPixel(display);
        return new ScreenInfo(width, height, depth);
    }

    private sealed record ScreenCaptureKitMetadata(int DisplayX, int DisplayY, int DisplayWidth, int DisplayHeight);
}
