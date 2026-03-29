using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenshotService : IScreenshotService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hDC, int nIndex);

    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int BITSPIXEL = 12;

    public byte[] TakeScreenshot()
    {
        var info = GetScreenInfo();
        using var bmp = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(0, 0, 0, 0, new Size(info.Width, info.Height));
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public ScreenInfo GetScreenInfo()
    {
        IntPtr hDC = GetDC(IntPtr.Zero);
        try
        {
            int width = GetDeviceCaps(hDC, HORZRES);
            int height = GetDeviceCaps(hDC, VERTRES);
            int depth = GetDeviceCaps(hDC, BITSPIXEL);
            return new ScreenInfo(width, height, depth);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hDC);
        }
    }
}
