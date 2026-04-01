namespace AIDeskAssistant.Services;

public interface IUiAutomationService
{
    /// <summary>Clicks an item from the Apple menu by title.</summary>
    void ClickAppleMenuItem(IReadOnlyList<string> titles);

    /// <summary>Clicks a System Settings sidebar item by title.</summary>
    void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles);
}