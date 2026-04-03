namespace AIDeskAssistant.Services;

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
}