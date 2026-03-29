using AIDeskAssistant.Platform.MacOS;
using AIDeskAssistant.Platform.Windows;
using AIDeskAssistant.Services;

namespace AIDeskAssistant;

/// <summary>Creates platform-appropriate service implementations at runtime.</summary>
internal static class PlatformServiceFactory
{
    public static IScreenshotService CreateScreenshotService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsScreenshotService();
        if (OperatingSystem.IsMacOS())   return new MacOSScreenshotService();
        throw new PlatformNotSupportedException(
            "AIDeskAssistant currently supports Windows and macOS only.");
    }

    public static IMouseService CreateMouseService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsMouseService();
        if (OperatingSystem.IsMacOS())   return new MacOSMouseService();
        throw new PlatformNotSupportedException(
            "AIDeskAssistant currently supports Windows and macOS only.");
    }

    public static IKeyboardService CreateKeyboardService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsKeyboardService();
        if (OperatingSystem.IsMacOS())   return new MacOSKeyboardService();
        throw new PlatformNotSupportedException(
            "AIDeskAssistant currently supports Windows and macOS only.");
    }

    public static ITerminalService CreateTerminalService() => new ProcessTerminalService();

    public static IWindowService CreateWindowService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsWindowService();
        if (OperatingSystem.IsMacOS())   return new MacOSWindowService();
        throw new PlatformNotSupportedException(
            "AIDeskAssistant currently supports Windows and macOS only.");
    }
}
