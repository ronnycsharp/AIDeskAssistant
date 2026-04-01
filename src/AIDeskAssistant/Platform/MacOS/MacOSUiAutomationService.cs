using System.Runtime.Versioning;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSUiAutomationService : IUiAutomationService
{
    private const int ScriptTimeoutMs = 15_000;

    private const string AppleMenuScript =
        """
        import AppKit
        import ApplicationServices
        import Foundation

        func fail(_ message: String) -> Never {
            FileHandle.standardError.write(Data((message + "\n").utf8))
            exit(1)
        }

        func normalized(_ value: String) -> String {
            value
                .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        func matches(_ title: String?, requestedTitles: [String]) -> Bool {
            guard let title else { return false }
            let normalizedTitle = normalized(title)
            return requestedTitles.contains { candidate in
                let normalizedCandidate = normalized(candidate)
                return normalizedTitle == normalizedCandidate
                    || normalizedTitle.contains(normalizedCandidate)
                    || normalizedCandidate.contains(normalizedTitle)
            }
        }

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func elementAttribute(_ element: AXUIElement, _ name: CFString) -> AXUIElement? {
            guard let value = attribute(element, name) else { return nil }
            return unsafeBitCast(value, to: AXUIElement.self)
        }

        func elementArrayAttribute(_ element: AXUIElement, _ name: CFString) -> [AXUIElement] {
            guard let values = attribute(element, name) as? [AnyObject] else { return [] }
            return values.map { unsafeBitCast($0, to: AXUIElement.self) }
        }

        func children(of element: AXUIElement) -> [AXUIElement] {
            elementArrayAttribute(element, kAXChildrenAttribute as CFString)
        }

        func title(of element: AXUIElement) -> String? {
            attribute(element, kAXTitleAttribute as CFString) as? String
        }

        func role(of element: AXUIElement) -> String? {
            attribute(element, kAXRoleAttribute as CFString) as? String
        }

        func descendants(of root: AXUIElement, depth: Int = 0) -> [AXUIElement] {
            if depth > 12 { return [] }
            let directChildren = children(of: root)
            return directChildren + directChildren.flatMap { descendants(of: $0, depth: depth + 1) }
        }

        @discardableResult
        func press(_ element: AXUIElement) -> Bool {
            AXUIElementPerformAction(element, kAXPressAction as CFString) == .success
        }

        let requestedTitles = Array(CommandLine.arguments.dropFirst())
        if requestedTitles.isEmpty { fail("At least one title is required.") }

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement automation.")
        }

        guard let frontmostApp = NSWorkspace.shared.frontmostApplication else {
            fail("Could not determine the frontmost application.")
        }

        let appElement = AXUIElementCreateApplication(frontmostApp.processIdentifier)
        guard let menuBar = elementAttribute(appElement, kAXMenuBarAttribute as CFString) else {
            fail("Could not access the frontmost application's menu bar.")
        }

        let menuBarItems = children(of: menuBar)
        guard let appleMenuBarItem = menuBarItems.first else {
            fail("Could not find the Apple menu bar item.")
        }

        guard press(appleMenuBarItem) else {
            fail("Could not open the Apple menu.")
        }

        usleep(400_000)

        let menuItems = descendants(of: appElement).filter { role(of: $0) == kAXMenuItemRole as String }

        guard let target = menuItems.first(where: { matches(title(of: $0), requestedTitles: requestedTitles) }) else {
            let availableTitles = menuItems.compactMap { title(of: $0) }.joined(separator: ", ")
            fail("Could not find a matching Apple menu item. Available items: \(availableTitles)")
        }

        guard press(target) else {
            fail("Could not click the Apple menu item.")
        }

        print(title(of: target) ?? requestedTitles[0])
        """;

    private const string SystemSettingsSidebarScript =
        """
        import AppKit
        import ApplicationServices
        import Foundation

        func fail(_ message: String) -> Never {
            FileHandle.standardError.write(Data((message + "\n").utf8))
            exit(1)
        }

        func normalized(_ value: String) -> String {
            value
                .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        func matches(_ title: String?, requestedTitles: [String]) -> Bool {
            guard let title else { return false }
            let normalizedTitle = normalized(title)
            return requestedTitles.contains { candidate in
                let normalizedCandidate = normalized(candidate)
                return normalizedTitle == normalizedCandidate
                    || normalizedTitle.contains(normalizedCandidate)
                    || normalizedCandidate.contains(normalizedTitle)
            }
        }

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func elementAttribute(_ element: AXUIElement, _ name: CFString) -> AXUIElement? {
            guard let value = attribute(element, name) else { return nil }
            return unsafeBitCast(value, to: AXUIElement.self)
        }

        func elementArrayAttribute(_ element: AXUIElement, _ name: CFString) -> [AXUIElement] {
            guard let values = attribute(element, name) as? [AnyObject] else { return [] }
            return values.map { unsafeBitCast($0, to: AXUIElement.self) }
        }

        func children(of element: AXUIElement) -> [AXUIElement] {
            elementArrayAttribute(element, kAXChildrenAttribute as CFString)
        }

        func title(of element: AXUIElement) -> String? {
            attribute(element, kAXTitleAttribute as CFString) as? String
        }

        func role(of element: AXUIElement) -> String? {
            attribute(element, kAXRoleAttribute as CFString) as? String
        }

        func parent(of element: AXUIElement) -> AXUIElement? {
            elementAttribute(element, kAXParentAttribute as CFString)
        }

        func actions(of element: AXUIElement) -> [String] {
            var names: CFArray?
            let error = AXUIElementCopyActionNames(element, &names)
            return error == .success ? (names as? [String] ?? []) : []
        }

        func setValue(_ element: AXUIElement, _ value: String) -> Bool {
            AXUIElementSetAttributeValue(element, kAXValueAttribute as CFString, value as CFTypeRef) == .success
        }

        func pressReturnKey() {
            guard let source = CGEventSource(stateID: .hidSystemState),
                  let keyDown = CGEvent(keyboardEventSource: source, virtualKey: 36, keyDown: true),
                  let keyUp = CGEvent(keyboardEventSource: source, virtualKey: 36, keyDown: false) else {
                return
            }

            keyDown.post(tap: .cghidEventTap)
            keyUp.post(tap: .cghidEventTap)
        }

        func frame(of element: AXUIElement) -> CGRect? {
            guard let value = attribute(element, "AXFrame" as CFString) else { return nil }
            let axValue = value as! AXValue
            var frame = CGRect.zero
            return AXValueGetValue(axValue, .cgRect, &frame) ? frame : nil
        }

        func descendants(of root: AXUIElement, depth: Int = 0) -> [AXUIElement] {
            if depth > 16 { return [] }
            let directChildren = children(of: root)
            return directChildren + directChildren.flatMap { descendants(of: $0, depth: depth + 1) }
        }

        func matchingElements(in window: AXUIElement, requestedTitles: [String]) -> [AXUIElement] {
            descendants(of: window).filter { matches(title(of: $0), requestedTitles: requestedTitles) }
        }

        func pressableAncestor(startingAt element: AXUIElement) -> AXUIElement? {
            var current: AXUIElement? = element
            var depth = 0
            while let currentElement = current, depth < 12 {
                if actions(of: currentElement).contains(kAXPressAction as String) {
                    return currentElement
                }
                current = parent(of: currentElement)
                depth += 1
            }
            return nil
        }

        @discardableResult
        func press(_ element: AXUIElement) -> Bool {
            AXUIElementPerformAction(element, kAXPressAction as CFString) == .success
        }

        func runningSystemSettingsApp() -> NSRunningApplication? {
            NSWorkspace.shared.runningApplications.first(where: {
                $0.bundleIdentifier == "com.apple.SystemSettings" || $0.bundleIdentifier == "com.apple.systempreferences"
            })
        }

        let requestedTitles = Array(CommandLine.arguments.dropFirst())
        if requestedTitles.isEmpty { fail("At least one title is required.") }

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement automation.")
        }

        if runningSystemSettingsApp() == nil {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            process.arguments = ["-a", "System Settings"]
            try process.run()
            process.waitUntilExit()
            usleep(800_000)
        }

        guard let settingsApp = runningSystemSettingsApp() else {
            fail("System Settings is not running.")
        }

                _ = settingsApp.activate()
        usleep(1_000_000)

        let appElement = AXUIElementCreateApplication(settingsApp.processIdentifier)
                let windows = elementArrayAttribute(appElement, kAXWindowsAttribute as CFString)
                guard let window = windows.first else {
            fail("Could not access the System Settings window.")
        }

        var matchesInWindow = matchingElements(in: window, requestedTitles: requestedTitles)

        if matchesInWindow.isEmpty {
            let searchFields = descendants(of: window).filter { role(of: $0) == kAXTextFieldRole as String }
            let sortedSearchFields = searchFields.sorted {
                let leftFrame = frame(of: $0) ?? .null
                let rightFrame = frame(of: $1) ?? .null
                if leftFrame.minY == rightFrame.minY {
                    return leftFrame.minX < rightFrame.minX
                }
                return leftFrame.minY < rightFrame.minY
            }

            if let searchField = sortedSearchFields.first {
                _ = pressableAncestor(startingAt: searchField).map(press)
                usleep(300_000)

                for candidate in requestedTitles {
                    guard setValue(searchField, candidate) else { continue }

                    usleep(500_000)
                    matchesInWindow = matchingElements(in: window, requestedTitles: requestedTitles)
                    if !matchesInWindow.isEmpty {
                        break
                    }

                    pressReturnKey()
                    usleep(1_000_000)
                    matchesInWindow = matchingElements(in: window, requestedTitles: requestedTitles)
                    if !matchesInWindow.isEmpty {
                        break
                    }

                    print(candidate)
                    exit(0)
                }
            }
        }

        let sortedMatches = matchesInWindow.sorted {
            let leftFrame = frame(of: $0) ?? .null
            let rightFrame = frame(of: $1) ?? .null
            if leftFrame.minX == rightFrame.minX {
                return leftFrame.minY < rightFrame.minY
            }
            return leftFrame.minX < rightFrame.minX
        }

        guard let match = sortedMatches.first,
              let target = pressableAncestor(startingAt: match) ?? sortedMatches.first else {
            let availableTitles = descendants(of: window).compactMap { title(of: $0) }.filter { !$0.isEmpty }.prefix(80)
            fail("Could not find a matching System Settings sidebar item. Available titles: \(availableTitles.joined(separator: ", "))")
        }

        guard press(target) else {
            fail("Could not click the System Settings sidebar item.")
        }

        print(title(of: match) ?? requestedTitles[0])
        """;

    public void ClickAppleMenuItem(IReadOnlyList<string> titles)
        => RunSwiftScript(AppleMenuScript, titles);

    public void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles)
        => RunSwiftScript(SystemSettingsSidebarScript, titles);

    private static void RunSwiftScript(string script, IReadOnlyList<string> arguments)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"aideskassistant_ax_{Guid.NewGuid():N}.swift");
        File.WriteAllText(scriptPath, script);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("swift")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add(scriptPath);
            foreach (string argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Swift accessibility helper.");

            if (!process.WaitForExit(ScriptTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Timed out while running the accessibility helper.");
            }

            string stdout = process.StandardOutput.ReadToEnd().Trim();
            string stderr = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Accessibility helper failed." : stderr);

            if (string.IsNullOrWhiteSpace(stdout))
                return;
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }
}