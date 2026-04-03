using System.Runtime.Versioning;
using AIDeskAssistant.Platform.MacOS;

namespace AIDeskAssistant.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSKeyboardServiceTests
{
    [Fact]
    public void BuildTypingActions_SplitsNewlinesAndTabsIntoKeyActions()
    {
        IReadOnlyList<MacOSTypingAction> actions = MacOSKeyboardService.BuildTypingActions("Erste Zeile\nZweite Zeile\r\n\rDritte\tSpalte");

        Assert.Equal(
            [
                MacOSTypingAction.Text("Erste Zeile"),
                MacOSTypingAction.Key("return"),
                MacOSTypingAction.Text("Zweite Zeile"),
                MacOSTypingAction.Key("return"),
                MacOSTypingAction.Key("return"),
                MacOSTypingAction.Text("Dritte"),
                MacOSTypingAction.Key("tab"),
                MacOSTypingAction.Text("Spalte")
            ],
            actions);
    }

    [Fact]
    public void BuildTypingActions_ReturnsSingleTextAction_WhenNoControlCharactersExist()
    {
        IReadOnlyList<MacOSTypingAction> actions = MacOSKeyboardService.BuildTypingActions("Hallo Welt");

        Assert.Equal([MacOSTypingAction.Text("Hallo Welt")], actions);
    }
}