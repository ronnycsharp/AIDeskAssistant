using System.Runtime.Versioning;
using System.Text.Json;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSUiAutomationService : IUiAutomationService
{
    private const int ScriptTimeoutMs = 15_000;

    private const string FrontmostUiSummaryScript =
        """
        import AppKit
        import ApplicationServices
        import Foundation

        func fail(_ message: String) -> Never {
            FileHandle.standardError.write(Data((message + "\n").utf8))
            exit(1)
        }

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func stringAttribute(_ element: AXUIElement, _ name: CFString) -> String? {
            attribute(element, name) as? String
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

        func descendants(of root: AXUIElement, depth: Int = 0) -> [AXUIElement] {
            if depth > 10 { return [] }
            let directChildren = children(of: root)
            return directChildren + directChildren.flatMap { descendants(of: $0, depth: depth + 1) }
        }

        func pointAttribute(_ element: AXUIElement, _ name: CFString) -> CGPoint? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var point = CGPoint.zero
            return AXValueGetValue(axValue, .cgPoint, &point) ? point : nil
        }

        func sizeAttribute(_ element: AXUIElement, _ name: CFString) -> CGSize? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var size = CGSize.zero
            return AXValueGetValue(axValue, .cgSize, &size) ? size : nil
        }

        func frame(of element: AXUIElement) -> CGRect? {
            guard let position = pointAttribute(element, "AXPosition" as CFString),
                  let size = sizeAttribute(element, "AXSize" as CFString) else {
                return nil
            }

            let rect = CGRect(origin: position, size: size)
            return rect.width > 0 && rect.height > 0 ? rect : nil
        }

        func normalizedText(_ value: String?) -> String {
            guard let value else { return "" }
            return value
                .replacingOccurrences(of: "\n", with: " ")
                .replacingOccurrences(of: "  ", with: " ")
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        func role(of element: AXUIElement) -> String {
            normalizedText(stringAttribute(element, kAXRoleAttribute as CFString))
        }

        func subrole(of element: AXUIElement) -> String {
            normalizedText(stringAttribute(element, kAXSubroleAttribute as CFString))
        }

        func title(of element: AXUIElement) -> String {
            normalizedText(stringAttribute(element, kAXTitleAttribute as CFString))
        }

        func valueDescription(of element: AXUIElement) -> String {
            normalizedText(stringAttribute(element, kAXValueAttribute as CFString))
        }

        func matchesInterestingRole(_ role: String) -> Bool {
            [
                "AXWindow",
                "AXButton",
                "AXTextField",
                "AXTextArea",
                "AXStaticText",
                "AXGroup",
                "AXScrollArea",
                "AXOutline",
                "AXRow",
                "AXCell",
                "AXToolbar",
                "AXSplitGroup",
                "AXMenuBar",
                "AXMenuBarItem",
                "AXRadioButton",
                "AXCheckBox",
                "AXPopUpButton",
                "AXComboBox"
            ].contains(role)
        }

        struct SummaryLine {
            let orderY: CGFloat
            let orderX: CGFloat
            let text: String
        }

        struct CalculatorButtonLabel {
            let x: Int
            let y: Int
            let width: Int
            let height: Int
            let label: String
        }

        func approximateEquals(_ left: CGFloat, _ right: CGFloat, tolerance: CGFloat = 6.0) -> Bool {
            abs(left - right) <= tolerance
        }

        func appendUniqueAxisValue(_ values: inout [CGFloat], _ candidate: CGFloat, tolerance: CGFloat = 6.0) {
            guard !values.contains(where: { approximateEquals($0, candidate, tolerance: tolerance) }) else { return }
            values.append(candidate)
        }

        func calculatorButtonLabels(appName: String, windowFrame: CGRect, candidates: [AXUIElement]) -> [CalculatorButtonLabel] {
            let normalizedAppName = normalizedText(appName).lowercased()
            guard normalizedAppName == "calculator" || normalizedAppName == "rechner" else {
                return []
            }

            let keypadButtons = candidates.compactMap { element -> (CGRect, String, String)? in
                let elementRole = role(of: element)
                guard elementRole == "AXButton", let rect = frame(of: element) else { return nil }
                guard rect.width >= 40, rect.height >= 40 else { return nil }
                guard rect.minY >= windowFrame.minY + 110 else { return nil }
                let titleValue = title(of: element)
                let valueText = valueDescription(of: element)
                return (rect, titleValue, valueText)
            }

            guard keypadButtons.count >= 16 else {
                return []
            }

            let groupedRows = Dictionary(grouping: keypadButtons) { item in
                keypadButtons
                    .map { $0.0.minY }
                    .sorted()
                    .first(where: { approximateEquals($0, item.0.minY, tolerance: 8.0) }) ?? item.0.minY
            }

            let keypadRows = groupedRows.values
                .filter { $0.count >= 4 }
                .sorted { left, right in left[0].0.minY < right[0].0.minY }
                .suffix(5)
                .map { row in
                    row.sorted { left, right in left.0.minX < right.0.minX }.prefix(4)
                }

            guard keypadRows.count >= 5 else {
                return []
            }

            let keypadButtonsForLabels = keypadRows.flatMap { $0 }

            var rowValues: [CGFloat] = []
            var columnValues: [CGFloat] = []
            for item in keypadButtonsForLabels {
                appendUniqueAxisValue(&rowValues, item.0.minY, tolerance: 8.0)
                appendUniqueAxisValue(&columnValues, item.0.minX)
            }

            rowValues.sort()
            columnValues.sort()

            let rowLabels = [
                ["delete", "ac", "percent", "divide"],
                ["7", "8", "9", "multiply"],
                ["4", "5", "6", "minus"],
                ["1", "2", "3", "plus"],
                ["plus_minus", "0", "decimal", "equals"]
            ]

            return keypadButtonsForLabels.compactMap { item in
                let rect = item.0
                guard let rowIndex = rowValues.firstIndex(where: { approximateEquals($0, rect.minY) }), rowIndex < rowLabels.count else {
                    return nil
                }

                guard let columnIndex = columnValues.firstIndex(where: { approximateEquals($0, rect.minX) }), columnIndex < rowLabels[rowIndex].count else {
                    return nil
                }

                return CalculatorButtonLabel(
                    x: Int(rect.origin.x.rounded()),
                    y: Int(rect.origin.y.rounded()),
                    width: Int(rect.size.width.rounded()),
                    height: Int(rect.size.height.rounded()),
                    label: rowLabels[rowIndex][columnIndex])
            }
        }

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement inspection.")
        }

        guard let app = NSWorkspace.shared.frontmostApplication else {
            fail("Could not determine the frontmost application.")
        }

        let appName = app.localizedName ?? "<unknown>"
        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        let mainWindow = elementAttribute(appElement, kAXFocusedWindowAttribute as CFString)
            ?? elementAttribute(appElement, kAXMainWindowAttribute as CFString)
            ?? elementArrayAttribute(appElement, kAXWindowsAttribute as CFString).first

        var output: [String] = []
        output.append("Frontmost app: \(appName)")

        if let mainWindow, let windowFrame = frame(of: mainWindow) {
            let windowTitle = title(of: mainWindow)
            output.append(String(format: "Focused window: %@ at x=%.0f,y=%.0f,w=%.0f,h=%.0f", windowTitle.isEmpty ? "<untitled>" : windowTitle, windowFrame.origin.x, windowFrame.origin.y, windowFrame.size.width, windowFrame.size.height))

            let candidates = [mainWindow] + descendants(of: mainWindow)
            let calculatorLabels = calculatorButtonLabels(appName: appName, windowFrame: windowFrame, candidates: candidates)
            var summaries: [SummaryLine] = []
            var seen = Set<String>()

            for element in candidates {
                let elementRole = role(of: element)
                guard matchesInterestingRole(elementRole), let rect = frame(of: element) else { continue }

                let elementTitle = title(of: element)
                let elementValue = valueDescription(of: element)
                let elementSubrole = subrole(of: element)
                let calculatorLabel = calculatorLabels.first(where: {
                    $0.x == Int(rect.origin.x.rounded())
                        && $0.y == Int(rect.origin.y.rounded())
                        && $0.width == Int(rect.size.width.rounded())
                        && $0.height == Int(rect.size.height.rounded())
                })?.label

                var labelParts: [String] = [elementRole]
                if !elementSubrole.isEmpty { labelParts.append(elementSubrole) }
                if !elementTitle.isEmpty { labelParts.append("title=\(elementTitle)") }
                if !elementValue.isEmpty && elementValue != elementTitle { labelParts.append("value=\(elementValue)") }
                if let calculatorLabel, !calculatorLabel.isEmpty { labelParts.append("calculator_key=\(calculatorLabel)") }
                labelParts.append(String(format: "x=%.0f,y=%.0f,w=%.0f,h=%.0f", rect.origin.x, rect.origin.y, rect.size.width, rect.size.height))

                let line = labelParts.joined(separator: " | ")
                if seen.insert(line).inserted {
                    summaries.append(SummaryLine(orderY: rect.origin.y, orderX: rect.origin.x, text: line))
                }
            }

            let sorted = summaries
                .sorted { lhs, rhs in
                    if lhs.orderY == rhs.orderY { return lhs.orderX < rhs.orderX }
                    return lhs.orderY < rhs.orderY
                }
                .prefix(40)

            output.append("Visible UI elements:")
            for summary in sorted {
                output.append("- \(summary.text)")
            }
        }
        else {
            output.append("Focused window unavailable.")
        }

        print(output.joined(separator: "\n"))
        """;

    private const string DockApplicationScript =
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
            if depth > 10 { return [] }
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

        guard let dockApp = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == "com.apple.dock" }) else {
            fail("The Dock is not running.")
        }

        let dockElement = AXUIElementCreateApplication(dockApp.processIdentifier)
        let candidates = descendants(of: dockElement).filter {
            role(of: $0) == kAXButtonRole as String || role(of: $0) == "AXUIElement"
        }

        guard let target = candidates.first(where: { matches(title(of: $0), requestedTitles: requestedTitles) }) else {
            let availableTitles = candidates.compactMap { title(of: $0) }.filter { !$0.isEmpty }.joined(separator: ", ")
            fail("Could not find a matching Dock application. Available items: \(availableTitles)")
        }

        guard press(target) else {
            fail("Could not click the Dock application.")
        }

        print(title(of: target) ?? requestedTitles[0])
        """;

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

    public string SummarizeFrontmostUiElements()
        => RunSwiftScriptAndCaptureOutput(FrontmostUiSummaryScript, []);

    private const string FocusFrontmostWindowContentScript =
        """
        import AppKit
        import ApplicationServices
        import CoreGraphics
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

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func stringAttribute(_ element: AXUIElement, _ name: CFString) -> String? {
            attribute(element, name) as? String
        }

        func boolAttribute(_ element: AXUIElement, _ name: CFString) -> Bool? {
            attribute(element, name) as? Bool
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

        func descendants(of root: AXUIElement, depth: Int = 0) -> [AXUIElement] {
            if depth > 18 { return [] }
            let directChildren = children(of: root)
            return directChildren + directChildren.flatMap { descendants(of: $0, depth: depth + 1) }
        }

        func frame(of element: AXUIElement) -> CGRect? {
            guard let value = attribute(element, "AXFrame" as CFString) else { return nil }
            let axValue = value as! AXValue
            var frame = CGRect.zero
            return AXValueGetValue(axValue, .cgRect, &frame) ? frame : nil
        }

        func actions(of element: AXUIElement) -> [String] {
            var names: CFArray?
            let error = AXUIElementCopyActionNames(element, &names)
            return error == .success ? (names as? [String] ?? []) : []
        }

        @discardableResult
        func press(_ element: AXUIElement) -> Bool {
            AXUIElementPerformAction(element, kAXPressAction as CFString) == .success
        }

        @discardableResult
        func raise(_ element: AXUIElement) -> Bool {
            AXUIElementPerformAction(element, kAXRaiseAction as CFString) == .success
        }

        @discardableResult
        func focus(_ element: AXUIElement) -> Bool {
            AXUIElementSetAttributeValue(element, kAXFocusedAttribute as CFString, kCFBooleanTrue) == .success
        }

        @discardableResult
        func setFocusedUIElement(_ app: AXUIElement, _ element: AXUIElement) -> Bool {
            AXUIElementSetAttributeValue(app, kAXFocusedUIElementAttribute as CFString, element) == .success
        }

        func click(at point: CGPoint) -> Bool {
            guard let source = CGEventSource(stateID: .hidSystemState),
                  let move = CGEvent(mouseEventSource: source, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left),
                  let down = CGEvent(mouseEventSource: source, mouseType: .leftMouseDown, mouseCursorPosition: point, mouseButton: .left),
                  let up = CGEvent(mouseEventSource: source, mouseType: .leftMouseUp, mouseCursorPosition: point, mouseButton: .left) else {
                return false
            }

            move.post(tap: .cghidEventTap)
            usleep(120_000)
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
            return true
        }

        func lowercasedMetadata(for element: AXUIElement) -> String {
            [
                stringAttribute(element, kAXRoleAttribute as CFString),
                stringAttribute(element, kAXSubroleAttribute as CFString),
                stringAttribute(element, kAXTitleAttribute as CFString),
                stringAttribute(element, kAXDescriptionAttribute as CFString),
                stringAttribute(element, kAXValueAttribute as CFString)
            ]
            .compactMap { $0 }
            .joined(separator: " ")
            .lowercased()
        }

        func roleScore(_ role: String?) -> Int {
            switch role {
            case "AXTextArea": return 10_000
            case "AXWebArea": return 9_000
            case "AXScrollArea": return 8_000
            case "AXTextField": return 4_000
            case "AXGroup": return 3_000
            case "AXLayoutArea": return 2_500
            case "AXSplitGroup": return 2_000
            default: return 0
            }
        }

        let expectedAppName = CommandLine.arguments.dropFirst().first ?? ""

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement automation.")
        }

        guard let frontmostApp = NSWorkspace.shared.frontmostApplication else {
            fail("Could not determine the frontmost application.")
        }

        let actualAppName = frontmostApp.localizedName ?? frontmostApp.bundleIdentifier ?? "unknown"
        if !expectedAppName.isEmpty {
            let actual = normalized(actualAppName)
            let expected = normalized(expectedAppName)
            guard actual == expected || actual.contains(expected) || expected.contains(actual) else {
                fail("Frontmost application mismatch. Expected '\(expectedAppName)', found '\(actualAppName)'.")
            }
        }

        let appElement = AXUIElementCreateApplication(frontmostApp.processIdentifier)
        let focusedWindow = elementAttribute(appElement, kAXFocusedWindowAttribute as CFString)
        let mainWindow = elementAttribute(appElement, kAXMainWindowAttribute as CFString)
        let allWindows = elementArrayAttribute(appElement, kAXWindowsAttribute as CFString)

        func shouldPreferMainWindow(focusedWindow: AXUIElement?, mainWindow: AXUIElement?) -> Bool {
            guard let focusedWindow,
                  let mainWindow,
                  let focusedFrame = frame(of: focusedWindow),
                  let mainFrame = frame(of: mainWindow) else {
                return false
            }

            let focusedArea = focusedFrame.width * focusedFrame.height
            let mainArea = mainFrame.width * mainFrame.height
            guard mainArea > 0 else {
                return false
            }

            return focusedArea < mainArea * 0.65
                || focusedFrame.width < 520
                || focusedFrame.height < 360
        }

        let window = shouldPreferMainWindow(focusedWindow: focusedWindow, mainWindow: mainWindow)
            ? mainWindow
            : (focusedWindow ?? mainWindow ?? allWindows.first)

        guard let targetWindow = window else {
            fail("Could not access the frontmost window.")
        }

        _ = raise(targetWindow)

        guard let windowFrame = frame(of: targetWindow), windowFrame.width >= 200, windowFrame.height >= 150 else {
            fail("Could not determine a usable frame for the frontmost window.")
        }

        let topExclusionHeight = min(180.0, windowFrame.height * 0.24)
        let minimumArea = max(12_000.0, windowFrame.width * windowFrame.height * 0.015)

        let candidateElements = descendants(of: targetWindow).compactMap { element -> (AXUIElement, CGRect, String?)? in
            guard let candidateFrame = frame(of: element) else { return nil }
            let role = stringAttribute(element, kAXRoleAttribute as CFString)
            let metadata = lowercasedMetadata(for: element)
            let area = candidateFrame.width * candidateFrame.height

            guard roleScore(role) > 0 else { return nil }
            guard area >= minimumArea else { return nil }
            guard candidateFrame.midX >= windowFrame.minX, candidateFrame.midX <= windowFrame.maxX else { return nil }
            guard candidateFrame.midY >= windowFrame.minY, candidateFrame.midY <= windowFrame.maxY else { return nil }
            guard candidateFrame.midY <= windowFrame.maxY - topExclusionHeight else { return nil }
            guard !metadata.contains("search"), !metadata.contains("suche"), !metadata.contains("find") else { return nil }
            guard !metadata.contains("toolbar"), !metadata.contains("ribbon") else { return nil }
            return (element, candidateFrame, role)
        }

        let sortedCandidates = candidateElements.sorted { left, right in
            let leftScore = roleScore(left.2) + Int((left.1.width * left.1.height) / 1_000.0)
            let rightScore = roleScore(right.2) + Int((right.1.width * right.1.height) / 1_000.0)

            if leftScore == rightScore {
                return left.1.midY < right.1.midY
            }

            return leftScore > rightScore
        }

        let fallbackFrame = CGRect(
            x: windowFrame.minX + max(40.0, windowFrame.width * 0.15),
            y: windowFrame.minY + max(40.0, windowFrame.height * 0.18),
            width: max(80.0, windowFrame.width * 0.70),
            height: max(80.0, windowFrame.height * 0.52))

        let chosenElement = sortedCandidates.first?.0
        let chosenFrame = sortedCandidates.first?.1 ?? fallbackFrame
        let chosenRole = sortedCandidates.first?.2 ?? "fallback-window-content"
        let targetPoint = CGPoint(x: chosenFrame.midX, y: chosenFrame.midY)

        var focused = false
        if let chosenElement {
            focused = focus(chosenElement) || setFocusedUIElement(appElement, chosenElement) || press(chosenElement)
        }

        if !click(at: targetPoint) {
            fail("Could not click the frontmost window content area.")
        }

        let metadata = chosenElement.map(lowercasedMetadata(for:)) ?? ""
        print("Focused frontmost window content in '\(actualAppName)'. role=\(chosenRole) point=(\(Int(targetPoint.x)), \(Int(targetPoint.y))) focused=\(focused) metadata=\(metadata)")
        """;

    private const string FindFrontmostUiElementsScript =
        """
        import AppKit
        import ApplicationServices
        import Foundation

        struct ElementInfo: Codable {
            let role: String
            let title: String
            let value: String
            let x: Int?
            let y: Int?
            let width: Int?
            let height: Int?
            let isFocused: Bool
            let isEnabled: Bool
        }

        func fail(_ message: String) -> Never {
            FileHandle.standardError.write(Data((message + "\n").utf8))
            exit(1)
        }

        func normalized(_ value: String?) -> String {
            (value ?? "")
                .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        func matches(_ value: String?, filter: String) -> Bool {
            if filter.isEmpty { return true }
            return normalized(value).contains(normalized(filter))
        }

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func stringAttribute(_ element: AXUIElement, _ name: CFString) -> String? {
            attribute(element, name) as? String
        }

        func boolAttribute(_ element: AXUIElement, _ name: CFString) -> Bool? {
            attribute(element, name) as? Bool
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

        func descendants(of root: AXUIElement, depth: Int = 0) -> [AXUIElement] {
            if depth > 16 { return [] }
            let directChildren = children(of: root)
            return directChildren + directChildren.flatMap { descendants(of: $0, depth: depth + 1) }
        }

        func pointAttribute(_ element: AXUIElement, _ name: CFString) -> CGPoint? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var point = CGPoint.zero
            return AXValueGetValue(axValue, .cgPoint, &point) ? point : nil
        }

        func sizeAttribute(_ element: AXUIElement, _ name: CFString) -> CGSize? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var size = CGSize.zero
            return AXValueGetValue(axValue, .cgSize, &size) ? size : nil
        }

        func frame(of element: AXUIElement) -> CGRect? {
            guard let position = pointAttribute(element, kAXPositionAttribute as CFString),
                  let size = sizeAttribute(element, kAXSizeAttribute as CFString) else {
                return nil
            }

            return CGRect(origin: position, size: size)
        }

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement inspection.")
        }

        let titleFilter = CommandLine.arguments.dropFirst().first ?? ""
        let roleFilter = CommandLine.arguments.dropFirst(2).first ?? ""
        let valueFilter = CommandLine.arguments.dropFirst(3).first ?? ""

        guard let app = NSWorkspace.shared.frontmostApplication else {
            fail("Could not determine the frontmost application.")
        }

        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        let window = elementAttribute(appElement, kAXFocusedWindowAttribute as CFString)
            ?? elementAttribute(appElement, kAXMainWindowAttribute as CFString)
            ?? elementArrayAttribute(appElement, kAXWindowsAttribute as CFString).first

        let candidates = window.map { [$0] + descendants(of: $0) } ?? []
            let matchingElements = candidates.compactMap { element -> ElementInfo? in
            let role = stringAttribute(element, kAXRoleAttribute as CFString) ?? ""
            let title = stringAttribute(element, kAXTitleAttribute as CFString) ?? ""
            let value = stringAttribute(element, kAXValueAttribute as CFString) ?? ""
            guard matches(role, filter: roleFilter), matches(title, filter: titleFilter), matches(value, filter: valueFilter) else {
                return nil
            }

            let elementFrame = frame(of: element)
            return ElementInfo(
                role: role,
                title: title,
                value: value,
                x: elementFrame.map { Int($0.origin.x.rounded()) },
                y: elementFrame.map { Int($0.origin.y.rounded()) },
                width: elementFrame.map { Int($0.size.width.rounded()) },
                height: elementFrame.map { Int($0.size.height.rounded()) },
                isFocused: boolAttribute(element, kAXFocusedAttribute as CFString) ?? false,
                isEnabled: boolAttribute(element, kAXEnabledAttribute as CFString) ?? true)
        }
        .sorted {
            if ($0.y ?? Int.max) == ($1.y ?? Int.max) {
                return ($0.x ?? Int.max) < ($1.x ?? Int.max)
            }

            return ($0.y ?? Int.max) < ($1.y ?? Int.max)
        }

        let data = try JSONEncoder().encode(Array(matchingElements.prefix(40)))
        print(String(data: data, encoding: .utf8) ?? "[]")
        """;

    private const string GetFocusedUiElementScript =
        """
        import AppKit
        import ApplicationServices
        import Foundation

        struct ElementInfo: Codable {
            let role: String
            let title: String
            let value: String
            let x: Int?
            let y: Int?
            let width: Int?
            let height: Int?
            let isFocused: Bool
            let isEnabled: Bool
        }

        func fail(_ message: String) -> Never {
            FileHandle.standardError.write(Data((message + "\n").utf8))
            exit(1)
        }

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func stringAttribute(_ element: AXUIElement, _ name: CFString) -> String? {
            attribute(element, name) as? String
        }

        func boolAttribute(_ element: AXUIElement, _ name: CFString) -> Bool? {
            attribute(element, name) as? Bool
        }

        func pointAttribute(_ element: AXUIElement, _ name: CFString) -> CGPoint? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var point = CGPoint.zero
            return AXValueGetValue(axValue, .cgPoint, &point) ? point : nil
        }

        func sizeAttribute(_ element: AXUIElement, _ name: CFString) -> CGSize? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var size = CGSize.zero
            return AXValueGetValue(axValue, .cgSize, &size) ? size : nil
        }

        func frame(of element: AXUIElement) -> CGRect? {
            guard let position = pointAttribute(element, kAXPositionAttribute as CFString),
                  let size = sizeAttribute(element, kAXSizeAttribute as CFString) else {
                return nil
            }

            return CGRect(origin: position, size: size)
        }

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement inspection.")
        }

        guard let app = NSWorkspace.shared.frontmostApplication else {
            fail("Could not determine the frontmost application.")
        }

        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        guard let focusedValue = attribute(appElement, kAXFocusedUIElementAttribute as CFString) else {
            print("")
            exit(0)
        }

        let focusedElement = unsafeBitCast(focusedValue, to: AXUIElement.self)
        let elementFrame = frame(of: focusedElement)
        let info = ElementInfo(
            role: stringAttribute(focusedElement, kAXRoleAttribute as CFString) ?? "",
            title: stringAttribute(focusedElement, kAXTitleAttribute as CFString) ?? "",
            value: stringAttribute(focusedElement, kAXValueAttribute as CFString) ?? "",
            x: elementFrame.map { Int($0.origin.x.rounded()) },
            y: elementFrame.map { Int($0.origin.y.rounded()) },
            width: elementFrame.map { Int($0.size.width.rounded()) },
            height: elementFrame.map { Int($0.size.height.rounded()) },
            isFocused: boolAttribute(focusedElement, kAXFocusedAttribute as CFString) ?? true,
            isEnabled: boolAttribute(focusedElement, kAXEnabledAttribute as CFString) ?? true)

        let data = try JSONEncoder().encode(info)
        print(String(data: data, encoding: .utf8) ?? "")
        """;

    private const string ClickFrontmostUiElementScript =
        """
        import AppKit
        import ApplicationServices
        import CoreGraphics
        import Foundation

        func fail(_ message: String) -> Never {
            FileHandle.standardError.write(Data((message + "\n").utf8))
            exit(1)
        }

        func normalized(_ value: String?) -> String {
            (value ?? "")
                .folding(options: [.caseInsensitive, .diacriticInsensitive], locale: .current)
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }

        func matches(_ value: String?, filter: String) -> Bool {
            if filter.isEmpty { return true }
            return normalized(value).contains(normalized(filter))
        }

        func attribute(_ element: AXUIElement, _ name: CFString) -> CFTypeRef? {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, name, &value)
            return error == .success ? value : nil
        }

        func stringAttribute(_ element: AXUIElement, _ name: CFString) -> String? {
            attribute(element, name) as? String
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

        func descendants(of root: AXUIElement, depth: Int = 0) -> [AXUIElement] {
            if depth > 16 { return [] }
            let directChildren = children(of: root)
            return directChildren + directChildren.flatMap { descendants(of: $0, depth: depth + 1) }
        }

        func pointAttribute(_ element: AXUIElement, _ name: CFString) -> CGPoint? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var point = CGPoint.zero
            return AXValueGetValue(axValue, .cgPoint, &point) ? point : nil
        }

        func sizeAttribute(_ element: AXUIElement, _ name: CFString) -> CGSize? {
            guard let raw = attribute(element, name) else { return nil }
            let axValue = raw as! AXValue
            var size = CGSize.zero
            return AXValueGetValue(axValue, .cgSize, &size) ? size : nil
        }

        func frame(of element: AXUIElement) -> CGRect? {
            guard let position = pointAttribute(element, kAXPositionAttribute as CFString),
                  let size = sizeAttribute(element, kAXSizeAttribute as CFString) else {
                return nil
            }

            return CGRect(origin: position, size: size)
        }

        func parent(of element: AXUIElement) -> AXUIElement? {
            elementAttribute(element, kAXParentAttribute as CFString)
        }

        func actions(of element: AXUIElement) -> [String] {
            var names: CFArray?
            let error = AXUIElementCopyActionNames(element, &names)
            return error == .success ? (names as? [String] ?? []) : []
        }

        @discardableResult
        func press(_ element: AXUIElement) -> Bool {
            AXUIElementPerformAction(element, kAXPressAction as CFString) == .success
        }

        @discardableResult
        func focus(_ element: AXUIElement) -> Bool {
            AXUIElementSetAttributeValue(element, kAXFocusedAttribute as CFString, kCFBooleanTrue) == .success
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

        func click(at point: CGPoint) -> Bool {
            guard let source = CGEventSource(stateID: .hidSystemState),
                  let move = CGEvent(mouseEventSource: source, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left),
                  let down = CGEvent(mouseEventSource: source, mouseType: .leftMouseDown, mouseCursorPosition: point, mouseButton: .left),
                  let up = CGEvent(mouseEventSource: source, mouseType: .leftMouseUp, mouseCursorPosition: point, mouseButton: .left) else {
                return false
            }

            move.post(tap: .cghidEventTap)
            usleep(120_000)
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
            return true
        }

        guard AXIsProcessTrusted() else {
            fail("Accessibility permission is required for AXUIElement automation.")
        }

        let titleFilter = CommandLine.arguments.dropFirst().first ?? ""
        let roleFilter = CommandLine.arguments.dropFirst(2).first ?? ""
        let valueFilter = CommandLine.arguments.dropFirst(3).first ?? ""
        let matchIndex = Int(CommandLine.arguments.dropFirst(4).first ?? "0") ?? 0

        guard let app = NSWorkspace.shared.frontmostApplication else {
            fail("Could not determine the frontmost application.")
        }

        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        let window = elementAttribute(appElement, kAXFocusedWindowAttribute as CFString)
            ?? elementAttribute(appElement, kAXMainWindowAttribute as CFString)
            ?? elementArrayAttribute(appElement, kAXWindowsAttribute as CFString).first
        guard let targetWindow = window else {
            fail("Could not access the frontmost window.")
        }

        let matchingElements = ([targetWindow] + descendants(of: targetWindow)).filter { element in
            let role = stringAttribute(element, kAXRoleAttribute as CFString)
            let title = stringAttribute(element, kAXTitleAttribute as CFString)
            let value = stringAttribute(element, kAXValueAttribute as CFString)
            return matches(role, filter: roleFilter) && matches(title, filter: titleFilter) && matches(value, filter: valueFilter)
        }
        .sorted {
            let leftFrame = frame(of: $0) ?? .null
            let rightFrame = frame(of: $1) ?? .null
            if leftFrame.minY == rightFrame.minY {
                return leftFrame.minX < rightFrame.minX
            }
            return leftFrame.minY < rightFrame.minY
        }

        guard matchIndex >= 0, matchIndex < matchingElements.count else {
            fail("No matching UI element found.")
        }

        let element = matchingElements[matchIndex]
        let target = pressableAncestor(startingAt: element) ?? element
        let elementFrame = frame(of: element)
        let clickPoint = elementFrame.map { CGPoint(x: $0.midX, y: $0.midY) }
        let clicked = press(target) || focus(element) || clickPoint.map(click(at:)) == true
        guard clicked else {
            fail("Could not activate the matching UI element.")
        }

        let role = stringAttribute(element, kAXRoleAttribute as CFString) ?? ""
        let title = stringAttribute(element, kAXTitleAttribute as CFString) ?? ""
        let value = stringAttribute(element, kAXValueAttribute as CFString) ?? ""
        if let elementFrame {
            print("Clicked UI element: role=\(role), title=\(title), value=\(value), x=\(Int(elementFrame.origin.x.rounded())), y=\(Int(elementFrame.origin.y.rounded())), width=\(Int(elementFrame.size.width.rounded())), height=\(Int(elementFrame.size.height.rounded()))")
        } else {
            print("Clicked UI element: role=\(role), title=\(title), value=\(value)")
        }
        """;

    public void ClickAppleMenuItem(IReadOnlyList<string> titles)
        => RunSwiftScript(AppleMenuScript, titles);

    public void ClickDockApplication(IReadOnlyList<string> titles)
        => RunSwiftScript(DockApplicationScript, titles);

    public void ClickSystemSettingsSidebarItem(IReadOnlyList<string> titles)
        => RunSwiftScript(SystemSettingsSidebarScript, titles);

    public string FocusFrontmostWindowContent(string? applicationName)
        => RunSwiftScriptAndCaptureOutput(FocusFrontmostWindowContentScript, string.IsNullOrWhiteSpace(applicationName)
            ? Array.Empty<string>()
            : [applicationName]);

    public IReadOnlyList<UiElementInfo> FindFrontmostUiElements(string? title = null, string? role = null, string? value = null)
        => ParseUiElementList(RunSwiftScriptAndCaptureOutput(FindFrontmostUiElementsScript, [title ?? string.Empty, role ?? string.Empty, value ?? string.Empty]));

    public UiElementInfo? GetFocusedUiElement()
        => ParseSingleUiElement(RunSwiftScriptAndCaptureOutput(GetFocusedUiElementScript, []));

    public string ClickFrontmostUiElement(string? title = null, string? role = null, string? value = null, int matchIndex = 0)
        => RunSwiftScriptAndCaptureOutput(ClickFrontmostUiElementScript, [title ?? string.Empty, role ?? string.Empty, value ?? string.Empty, matchIndex.ToString()]);

    private static void RunSwiftScript(string script, IReadOnlyList<string> arguments)
    {
        _ = RunSwiftScriptAndCaptureOutput(script, arguments);
    }

    private static string RunSwiftScriptAndCaptureOutput(string script, IReadOnlyList<string> arguments)
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
                return "Accessibility helper completed.";

            return stdout;
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }

    private static IReadOnlyList<UiElementInfo> ParseUiElementList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<UiElementInfo>();

        List<UiElementDto>? dtos = JsonSerializer.Deserialize<List<UiElementDto>>(json);
        return dtos?.Select(ToUiElementInfo).ToArray() ?? Array.Empty<UiElementInfo>();
    }

    private static UiElementInfo? ParseSingleUiElement(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        UiElementDto? dto = JsonSerializer.Deserialize<UiElementDto>(json);
        return dto is null ? null : ToUiElementInfo(dto);
    }

    private static UiElementInfo ToUiElementInfo(UiElementDto dto)
    {
        WindowBounds? bounds = dto.X.HasValue && dto.Y.HasValue && dto.Width.HasValue && dto.Height.HasValue
            ? new WindowBounds(dto.X.Value, dto.Y.Value, dto.Width.Value, dto.Height.Value)
            : null;

        return new UiElementInfo(dto.Role ?? string.Empty, dto.Title ?? string.Empty, dto.Value ?? string.Empty, bounds, dto.IsFocused, dto.IsEnabled);
    }

    private sealed class UiElementDto
    {
        public string? Role { get; set; }
        public string? Title { get; set; }
        public string? Value { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public bool IsFocused { get; set; }
        public bool IsEnabled { get; set; }
    }
}