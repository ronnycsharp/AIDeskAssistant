using System.Runtime.Versioning;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSWindowService : IWindowService
{
    private const int ScriptTimeoutMs = 10_000;

    public WindowBounds GetActiveWindowBounds()
    {
        string output = RunOsascript(
            """
            tell application "System Events"
                tell (first application process whose frontmost is true)
                    set candidateWindows to every window whose value of attribute "AXMinimized" is false
                    if (count of candidateWindows) is 0 then
                        set candidateWindows to windows
                    end if

                    if (count of candidateWindows) is 0 then
                        error "Could not access any window for the frontmost application."
                    end if

                    set bestWindow to item 1 of candidateWindows
                    set bestArea to 0

                    repeat with currentWindow in candidateWindows
                        set s to size of currentWindow
                        set currentArea to (item 1 of s) * (item 2 of s)
                        if currentArea > bestArea then
                            set bestArea to currentArea
                            set bestWindow to currentWindow
                        end if
                    end repeat

                    tell bestWindow
                        set p to position
                        set s to size
                        return (item 1 of p as text) & "," & (item 2 of p as text) & "," & (item 1 of s as text) & "," & (item 2 of s as text)
                    end tell
                end tell
            end tell
            """);

        string[] parts = output.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out int x)
            || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int width)
            || !int.TryParse(parts[3], out int height))
        {
            throw new InvalidOperationException($"Could not parse active window bounds: '{output}'");
        }

        return new WindowBounds(x, y, width, height);
    }

    public WindowHitTestResult? GetWindowAtPoint(int x, int y)
    {
        string output = RunOsascript(
            $$"""
            tell application "System Events"
                set cursorX to {{x}}
                set cursorY to {{y}}
                set bestApp to ""
                set bestTitle to ""
                set bestX to ""
                set bestY to ""
                set bestWidth to ""
                set bestHeight to ""
                set bestArea to 2147483647

                set proc to first application process whose frontmost is true
                repeat with currentWindow in windows of proc
                    try
                        set isMinimized to false
                        try
                            set isMinimized to value of attribute "AXMinimized" of currentWindow
                        end try

                        if isMinimized is false then
                            set p to position of currentWindow
                            set s to size of currentWindow
                            set leftEdge to item 1 of p
                            set topEdge to item 2 of p
                            set windowWidth to item 1 of s
                            set windowHeight to item 2 of s

                            if windowWidth > 0 and windowHeight > 0 and cursorX >= leftEdge and cursorX < (leftEdge + windowWidth) and cursorY >= topEdge and cursorY < (topEdge + windowHeight) then
                                set currentArea to windowWidth * windowHeight
                                if bestApp is "" or currentArea < bestArea then
                                    set bestArea to currentArea
                                    set bestApp to (name of proc as text)
                                    try
                                        set bestTitle to (name of currentWindow as text)
                                    on error
                                        set bestTitle to ""
                                    end try
                                    set bestX to leftEdge as text
                                    set bestY to topEdge as text
                                    set bestWidth to windowWidth as text
                                    set bestHeight to windowHeight as text
                                end if
                            end if
                        end if
                    end try
                end repeat

                if bestApp is "" then
                    return ""
                end if

                return bestApp & "||" & bestTitle & "||" & bestX & "||" & bestY & "||" & bestWidth & "||" & bestHeight
            end tell
            """);

        if (string.IsNullOrWhiteSpace(output))
            return null;

        string[] parts = output.Split("||", StringSplitOptions.None);
        if (parts.Length != 6
            || !int.TryParse(parts[2], out int windowX)
            || !int.TryParse(parts[3], out int windowY)
            || !int.TryParse(parts[4], out int windowWidth)
            || !int.TryParse(parts[5], out int windowHeight))
        {
            throw new InvalidOperationException($"Could not parse window at point '{x},{y}': '{output}'");
        }

        return new WindowHitTestResult(parts[0], parts[1], new WindowBounds(windowX, windowY, windowWidth, windowHeight));
    }

    public void MoveActiveWindow(int x, int y)
    {
        RunOsascript(
            $$"""
            tell application "System Events"
                tell (first application process whose frontmost is true)
                    tell front window
                        set position to { {{x}}, {{y}} }
                    end tell
                end tell
            end tell
            """);
    }

    public void ResizeActiveWindow(int width, int height)
    {
        RunOsascript(
            $$"""
            tell application "System Events"
                tell (first application process whose frontmost is true)
                    tell front window
                        set size to { {{width}}, {{height}} }
                    end tell
                end tell
            end tell
            """);
    }

    private static string RunOsascript(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("osascript")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start osascript.");

        process.StandardInput.WriteLine(script);
        process.StandardInput.Close();

        if (!process.WaitForExit(ScriptTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Timed out while controlling the active window.");
        }

        string stdout = process.StandardOutput.ReadToEnd().Trim();
        string stderr = process.StandardError.ReadToEnd().Trim();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "AppleScript failed." : stderr);

        return stdout;
    }
}
