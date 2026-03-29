namespace AIDeskAssistant.Services;

public interface IKeyboardService
{
    /// <summary>Types the given text string using the keyboard.</summary>
    void TypeText(string text);

    /// <summary>
    /// Sends a key combination such as "ctrl+c", "alt+F4", or a single key like "enter".
    /// Keys are separated by '+'. Supported modifiers: ctrl, alt, shift, win/cmd.
    /// </summary>
    void PressKey(string keyCombo);
}
