using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace AIDeskAssistant.Services;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "System.Drawing is intentionally enabled for this desktop app, including Unix support via runtime configuration.")]
internal static class ScreenshotHistoryComparer
{
    private const int FingerprintSize = 32;

    public static ScreenshotFingerprint CreateFingerprint(byte[] imageBytes)
    {
        try
        {
            using var input = new MemoryStream(imageBytes);
            using var image = Image.FromStream(input);
            using var bitmap = new Bitmap(FingerprintSize, FingerprintSize, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);

            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, 0, 0, FingerprintSize, FingerprintSize);

            byte[] samples = new byte[FingerprintSize * FingerprintSize];
            for (int y = 0; y < FingerprintSize; y++)
            {
                for (int x = 0; x < FingerprintSize; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    samples[(y * FingerprintSize) + x] = (byte)Math.Round((pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114));
                }
            }

            return new ScreenshotFingerprint(samples, false);
        }
        catch
        {
            return new ScreenshotFingerprint(SHA256.HashData(imageBytes), true);
        }
    }

    public static double CalculateSimilarity(ScreenshotFingerprint left, ScreenshotFingerprint right)
    {
        if (left.IsHashOnly || right.IsHashOnly)
            return left.Samples.SequenceEqual(right.Samples) ? 1d : 0d;

        if (left.Samples.Length != right.Samples.Length)
            return 0d;

        double totalDifference = 0d;
        for (int index = 0; index < left.Samples.Length; index++)
            totalDifference += Math.Abs(left.Samples[index] - right.Samples[index]);

        double averageDifference = totalDifference / left.Samples.Length;
        return Math.Clamp(1d - (averageDifference / 255d), 0d, 1d);
    }

    public static byte[]? CreateDifferenceVisualization(byte[] previousImageBytes, byte[] currentImageBytes)
    {
        try
        {
            using var previousInput = new MemoryStream(previousImageBytes);
            using var currentInput = new MemoryStream(currentImageBytes);
            using var previousImage = Image.FromStream(previousInput);
            using var currentImage = Image.FromStream(currentInput);
            using var previousBitmap = new Bitmap(previousImage, currentImage.Size);
            using var currentBitmap = new Bitmap(currentImage);
            using var diffBitmap = new Bitmap(currentBitmap.Width, currentBitmap.Height, PixelFormat.Format24bppRgb);

            for (int y = 0; y < diffBitmap.Height; y++)
            {
                for (int x = 0; x < diffBitmap.Width; x++)
                {
                    Color previous = previousBitmap.GetPixel(x, y);
                    Color current = currentBitmap.GetPixel(x, y);
                    int diff = Math.Abs(current.R - previous.R)
                        + Math.Abs(current.G - previous.G)
                        + Math.Abs(current.B - previous.B);

                    int intensity = Math.Clamp(diff / 3, 0, 255);
                    diffBitmap.SetPixel(x, y, Color.FromArgb(intensity, intensity, intensity));
                }
            }

            using var output = new MemoryStream();
            diffBitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record ScreenshotFingerprint(byte[] Samples, bool IsHashOnly);