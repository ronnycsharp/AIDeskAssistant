namespace AIDeskAssistant.Services;

using AIDeskAssistant.Models;

public readonly record struct UiElementInfo(
    string Role,
    string Title,
    string Value,
    WindowBounds? Bounds,
    bool IsFocused,
    bool IsEnabled);

public interface IUiAutomationService
{
    /// <summary>Returns a compact summary of visible UI elements for the frontmost macOS application.</summary>
    string SummarizeFrontmostUiElements();

    /// <summary>Clicks an application in the macOS Dock by title.</summary>
    void ClickDockApplication(IReadOnlyList<string> titles);

    /// <summary>Clicks an item from the Apple menu by title.</summary>
    void ClickAppleMenuItem(IReadOnlyList<string> titles);

    /// <summary>Clicks a System Settings sidebar item by title.</summary>
    void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles);

    /// <summary>Focuses a likely content area inside the current frontmost window.</summary>
    string FocusFrontmostWindowContent(string? applicationName);

    /// <summary>Finds matching UI elements in the frontmost application/window.</summary>
    IReadOnlyList<UiElementInfo> FindFrontmostUiElements(string? title = null, string? role = null, string? value = null);

    /// <summary>Returns the currently focused UI element for the frontmost application/window.</summary>
    UiElementInfo? GetFocusedUiElement();

    /// <summary>Clicks a matching UI element in the frontmost application/window.</summary>
    string ClickFrontmostUiElement(string? title = null, string? role = null, string? value = null, int matchIndex = 0);
}