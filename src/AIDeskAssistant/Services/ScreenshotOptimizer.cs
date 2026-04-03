using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AIDeskAssistant.Services;

internal sealed class ScreenshotOptimizer
{
    private const string PngMediaType = "image/png";
    private const string JpegMediaType = "image/jpeg";

    private readonly ScreenshotOptimizationOptions _options;

    public ScreenshotOptimizer(ScreenshotOptimizationOptions options)
    {
        _options = options;
    }

    public ScreenshotPayload Optimize(byte[] screenshotBytes)
    {
        if (OperatingSystem.IsMacOS())
            return OptimizeWithSips(screenshotBytes);

        if (OperatingSystem.IsWindows())
            return OptimizeWithSystemDrawing(screenshotBytes);

        return new ScreenshotPayload(
            screenshotBytes,
            PngMediaType,
            screenshotBytes.Length,
            screenshotBytes.Length,
            0,
            0);
    }

    internal static ScreenshotOptimizationOptions ReadFromEnvironment()
    {
        long jpegQuality = ReadInt("AIDESK_SCREENSHOT_JPEG_QUALITY", 100, 30, 100);
        return new ScreenshotOptimizationOptions(jpegQuality);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private ScreenshotPayload OptimizeWithSips(byte[] screenshotBytes)
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant_input_{Guid.NewGuid():N}.png");
        string outputPath = Path.Combine(Path.GetTempPath(), $"aideskassistant_output_{Guid.NewGuid():N}.jpg");

        try
        {
            File.WriteAllBytes(inputPath, screenshotBytes);

            var process = Process.Start(new ProcessStartInfo(
                "sips",
                [
                    "-s", "format", "jpeg",
                    "-s", "formatOptions", _options.JpegQuality.ToString(),
                    inputPath,
                    "--out", outputPath,
                ])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            process!.WaitForExit(10_000);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                return new ScreenshotPayload(screenshotBytes, PngMediaType, screenshotBytes.Length, screenshotBytes.Length, 0, 0);
            }

            byte[] optimizedBytes = File.ReadAllBytes(outputPath);
            (int width, int height) = ReadImageSizeWithSips(outputPath);

            if (optimizedBytes.Length >= screenshotBytes.Length)
            {
                (width, height) = ReadImageSizeWithSips(inputPath);
                return new ScreenshotPayload(screenshotBytes, PngMediaType, screenshotBytes.Length, screenshotBytes.Length, width, height);
            }

            return new ScreenshotPayload(optimizedBytes, JpegMediaType, screenshotBytes.Length, optimizedBytes.Length, width, height);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private ScreenshotPayload OptimizeWithSystemDrawing(byte[] screenshotBytes)
    {
        using var inputStream = new MemoryStream(screenshotBytes);
        using var image = Image.FromStream(inputStream);

        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.White);
        graphics.DrawImage(image, 0, 0, image.Width, image.Height);

        using var outputStream = new MemoryStream();
        ImageCodecInfo jpegEncoder = GetEncoder(ImageFormat.Jpeg);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, _options.JpegQuality);
        bitmap.Save(outputStream, jpegEncoder, encoderParameters);

        byte[] jpegBytes = outputStream.ToArray();
        byte[] finalBytes = jpegBytes.Length < screenshotBytes.Length ? jpegBytes : screenshotBytes;
        string mediaType = jpegBytes.Length < screenshotBytes.Length ? JpegMediaType : PngMediaType;
        int width = image.Width;
        int height = image.Height;

        return new ScreenshotPayload(
            finalBytes,
            mediaType,
            screenshotBytes.Length,
            finalBytes.Length,
            width,
            height);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static (int Width, int Height) ReadImageSizeWithSips(string path)
    {
        var process = Process.Start(new ProcessStartInfo(
            "sips",
            ["-g", "pixelWidth", "-g", "pixelHeight", path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        string output = process!.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);

        int width = 0;
        int height = 0;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("pixelWidth:", StringComparison.Ordinal))
                int.TryParse(line["pixelWidth:".Length..].Trim(), out width);
            else if (line.StartsWith("pixelHeight:", StringComparison.Ordinal))
                int.TryParse(line["pixelHeight:".Length..].Trim(), out height);
        }

        return (width, height);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ImageCodecInfo GetEncoder(ImageFormat format)
        => ImageCodecInfo.GetImageDecoders().First(codec => codec.FormatID == format.Guid);

    private static int ReadInt(string name, int defaultValue, int minValue, int maxValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(value, out int parsed))
            return defaultValue;

        return Math.Clamp(parsed, minValue, maxValue);
    }

}

internal sealed record ScreenshotOptimizationOptions(long JpegQuality);