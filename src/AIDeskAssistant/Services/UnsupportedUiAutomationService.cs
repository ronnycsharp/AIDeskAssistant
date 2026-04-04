namespace AIDeskAssistant.Services;

internal sealed class UnsupportedUiAutomationService : IUiAutomationService
{
    private readonly string _platformName;

    public UnsupportedUiAutomationService(string platformName)
    {
        _platformName = platformName;
    }

    public string SummarizeFrontmostUiElements()
        => $"Accessibility UI automation is not available on {_platformName}.";

    public void ClickDockApplication(IReadOnlyList<string> titles)
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");

    public void ClickAppleMenuItem(IReadOnlyList<string> titles)
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");

    public void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles)
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");

    public string FocusFrontmostWindowContent(string? applicationName)
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");

    public IReadOnlyList<UiElementInfo> FindFrontmostUiElements(string? title = null, string? role = null, string? value = null)
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");

    public UiElementInfo? GetFocusedUiElement()
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");

    public string ClickFrontmostUiElement(string? title = null, string? role = null, string? value = null, int matchIndex = 0)
        => throw new PlatformNotSupportedException($"Accessibility UI automation is not available on {_platformName}.");
}