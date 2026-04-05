import Cocoa
import AVFoundation
import WebKit

struct AssistantResponse: Decodable {
    let text: String?
    let audioBase64: String?
    let audioMimeType: String?
    let error: String?
    let usage: TokenUsage?
}

struct AssistantStreamEvent: Decodable {
    let type: String
    let text: String?
    let pcmBase64: String?
    let sampleRate: Int?
    let audioFormat: String?
    let error: String?
    let usage: TokenUsage?
}

struct AudioLiveStartResponse: Decodable {
    let sessionId: String
}

struct VoiceSettingsResponse: Decodable {
    let currentVoice: String
    let availableVoices: [String]
    let currentThinkingLevel: String
    let availableThinkingLevels: [String]
}

struct VoiceSelectionRequest: Encodable {
    let voice: String
}

struct ThinkingSelectionRequest: Encodable {
    let thinkingLevel: String
}

struct TokenUsage: Decodable {
    let inputTokens: Int?
    let inputTextTokens: Int?
    let inputAudioTokens: Int?
    let inputImageTokens: Int?
    let cachedInputTokens: Int?
    let outputTokens: Int?
    let outputTextTokens: Int?
    let outputAudioTokens: Int?
    let totalTokens: Int?
}

struct ActivitySnapshot: Decodable {
    let CurrentStep: String?
    let ActiveTool: String?
    let LastUpdatedUtc: String?
    let Entries: [ActivityEntry]
}

struct ActivityEntry: Decodable {
    let TimestampUtc: String?
    let Message: String
    let Kind: String?
    let ToolName: String?
}

struct ToolLogSnapshot: Decodable {
    let LastUpdatedUtc: String?
    let Entries: [ToolLogEntry]
}

struct ToolLogEntry: Decodable {
    let ToolCallId: String
    let ToolName: String
    let ArgumentsJson: String
    let Status: String
    let Result: String
    let StartedUtc: String?
    let CompletedUtc: String?
    let Screenshots: [ToolScreenshotArtifact]
}

struct ToolScreenshotArtifact: Decodable {
    let Kind: String
    let ImageFileName: String
    let MetaFileName: String?
    let Summary: String
    let MediaType: String?
    let RetentionStatus: String?
    let Similarity: String?
}

private struct AudioLevelMetrics {
    let peak: Double
    let rms: Double
}

final class StatusBarDiagnosticsLogger {
    private let fileURL: URL?
    private let queue = DispatchQueue(label: "aidesk.statusbar.logger")

    init(environment: [String: String] = ProcessInfo.processInfo.environment) {
        if let path = environment["AIDESK_MENU_BAR_LOG_FILE"], !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            fileURL = URL(fileURLWithPath: path)
        } else {
            fileURL = nil
        }
    }

    func log(_ message: String) {
        guard let fileURL else {
            return
        }

        queue.async {
            let directory = fileURL.deletingLastPathComponent()
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let line = "[\(ISO8601DateFormatter().string(from: Date()))] \(message)\n"
            let data = Data(line.utf8)

            if FileManager.default.fileExists(atPath: fileURL.path) {
                if let handle = try? FileHandle(forWritingTo: fileURL) {
                    defer { try? handle.close() }
                    _ = try? handle.seekToEnd()
                    try? handle.write(contentsOf: data)
                }
            } else {
                try? data.write(to: fileURL, options: .atomic)
            }
        }
    }
}

final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func cancelOperation(_ sender: Any?) {
        orderOut(sender)
    }
}

final class ActivityLogViewController: NSViewController, WKNavigationDelegate, WKScriptMessageHandler {
    private static let activityFilePath: String = {
        if let path = ProcessInfo.processInfo.environment["AIDESK_MENU_BAR_ACTIVITY_FILE"], !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return path
        }

        return URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("AIDeskAssistant", isDirectory: true)
            .appendingPathComponent("menu-bar-activity.json")
            .path
    }()

    private static let toolLogFilePath: String = {
        if let path = ProcessInfo.processInfo.environment["AIDESK_MENU_BAR_TOOL_LOG_FILE"], !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return path
        }

        if let sessionDirectory = ProcessInfo.processInfo.environment["AIDESK_MENU_BAR_DEBUG_SESSION_DIR"], !sessionDirectory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return URL(fileURLWithPath: sessionDirectory, isDirectory: true)
                .appendingPathComponent("menu-bar-tool-details.json")
                .path
        }

        return URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("AIDeskAssistant", isDirectory: true)
            .appendingPathComponent("menu-bar-tool-details.json")
            .path
    }()

    private let headerLabel = NSTextField(labelWithString: "AIDesk Tool Log")
    private let stepLabel = NSTextField(labelWithString: "Leerlauf")
    private let toolLabel = NSTextField(labelWithString: "Tool: -")
    private let updatedLabel = NSTextField(labelWithString: "Zuletzt aktualisiert: -")
    private let logScrollView = NSScrollView(frame: .zero)
    private lazy var logWebView: WKWebView = {
        let contentController = WKUserContentController()
        contentController.add(self, name: "toggleEntry")

        let configuration = WKWebViewConfiguration()
        configuration.userContentController = contentController

        let webView = WKWebView(frame: .zero, configuration: configuration)
        webView.navigationDelegate = self
        webView.setValue(false, forKey: "drawsBackground")
        webView.isInspectable = true
        return webView
    }()
    private var pollTimer: Timer?
    private var lastRenderedHTML = ""
    private var expandedEntryIds = Set<String>()

    deinit {
        logWebView.configuration.userContentController.removeScriptMessageHandler(forName: "toggleEntry")
    }

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: 760, height: 460))
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor(calibratedRed: 0.10, green: 0.11, blue: 0.14, alpha: 0.98).cgColor

        headerLabel.font = NSFont.systemFont(ofSize: 22, weight: .semibold)
        headerLabel.textColor = NSColor(calibratedRed: 0.96, green: 0.97, blue: 0.99, alpha: 1.0)

        stepLabel.font = NSFont.systemFont(ofSize: 15, weight: .semibold)
        stepLabel.textColor = NSColor(calibratedRed: 0.58, green: 0.82, blue: 0.96, alpha: 1.0)

        toolLabel.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        toolLabel.textColor = NSColor(calibratedRed: 0.78, green: 0.83, blue: 0.88, alpha: 1.0)

        updatedLabel.font = NSFont.systemFont(ofSize: 12, weight: .regular)
        updatedLabel.textColor = NSColor(calibratedRed: 0.64, green: 0.69, blue: 0.75, alpha: 1.0)

        logScrollView.borderType = .noBorder
        logScrollView.drawsBackground = false
        logScrollView.hasVerticalScroller = true
        logScrollView.autohidesScrollers = true
        logScrollView.documentView = logWebView

        view.addSubview(headerLabel)
        view.addSubview(stepLabel)
        view.addSubview(toolLabel)
        view.addSubview(updatedLabel)
        view.addSubview(logScrollView)
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        startPolling()
    }

    override func viewDidDisappear() {
        super.viewDidDisappear()
        pollTimer?.invalidate()
        pollTimer = nil
    }

    override func viewDidLayout() {
        super.viewDidLayout()
        let bounds = view.bounds
        headerLabel.frame = NSRect(x: 20, y: bounds.height - 42, width: bounds.width - 40, height: 28)
        stepLabel.frame = NSRect(x: 20, y: bounds.height - 72, width: bounds.width - 40, height: 20)
        toolLabel.frame = NSRect(x: 20, y: bounds.height - 94, width: bounds.width - 40, height: 18)
        updatedLabel.frame = NSRect(x: 20, y: bounds.height - 116, width: bounds.width - 40, height: 18)
        logScrollView.frame = NSRect(x: 20, y: 20, width: bounds.width - 40, height: bounds.height - 150)
        logWebView.frame = NSRect(origin: .zero, size: logScrollView.contentSize)
    }

    private func startPolling() {
        pollTimer?.invalidate()
        refreshSnapshot()
        pollTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.refreshSnapshot()
        }
    }

    private func refreshSnapshot() {
        let snapshot = loadActivitySnapshot()
        let toolSnapshot = loadToolLogSnapshot()
        render(activitySnapshot: snapshot, toolSnapshot: toolSnapshot)
    }

    private func loadActivitySnapshot() -> ActivitySnapshot? {
        let url = URL(fileURLWithPath: Self.activityFilePath)
        guard let data = try? Data(contentsOf: url) else {
            return nil
        }

        return try? JSONDecoder().decode(ActivitySnapshot.self, from: data)
    }

        private func loadToolLogSnapshot() -> ToolLogSnapshot? {
                let url = URL(fileURLWithPath: Self.toolLogFilePath)
                guard let data = try? Data(contentsOf: url) else {
                        return nil
                }

                return try? JSONDecoder().decode(ToolLogSnapshot.self, from: data)
        }

        private func render(activitySnapshot: ActivitySnapshot?, toolSnapshot: ToolLogSnapshot?) {
                guard let activitySnapshot else {
            stepLabel.stringValue = "Leerlauf"
            toolLabel.stringValue = "Tool: -"
            updatedLabel.stringValue = "Zuletzt aktualisiert: -"
                        renderHTML(buildLogHTML(activitySnapshot: nil, toolSnapshot: toolSnapshot))
            return
        }

                let currentStep = activitySnapshot.CurrentStep?.trimmingCharacters(in: .whitespacesAndNewlines)
        stepLabel.stringValue = currentStep?.isEmpty == false ? currentStep! : "Leerlauf"

                if let activeTool = activitySnapshot.ActiveTool?.trimmingCharacters(in: .whitespacesAndNewlines), !activeTool.isEmpty {
            toolLabel.stringValue = "Tool: \(activeTool)"
        } else {
            toolLabel.stringValue = "Tool: -"
        }

                if let lastUpdated = toolSnapshot?.LastUpdatedUtc ?? activitySnapshot.LastUpdatedUtc, !lastUpdated.isEmpty {
                        updatedLabel.stringValue = "Zuletzt aktualisiert: \(Self.formatDisplayTimestamp(lastUpdated))"
        } else {
            updatedLabel.stringValue = "Zuletzt aktualisiert: -"
        }

                renderHTML(buildLogHTML(activitySnapshot: activitySnapshot, toolSnapshot: toolSnapshot))
        }

        private func renderHTML(_ html: String) {
                guard html != lastRenderedHTML else {
                        return
                }

                lastRenderedHTML = html
                let baseURL = URL(fileURLWithPath: Self.toolLogFilePath).deletingLastPathComponent()
                logWebView.loadHTMLString(html, baseURL: baseURL)
        }

        private func buildLogHTML(activitySnapshot: ActivitySnapshot?, toolSnapshot: ToolLogSnapshot?) -> String {
                let events = (activitySnapshot?.Entries ?? []).suffix(8).reversed()
                let entries = (toolSnapshot?.Entries ?? []).reversed()
            let workflowMarkup = buildWorkflowMarkup(for: Array(entries))
                let eventMarkup: String

                if events.isEmpty {
                        eventMarkup = "<div class=\"empty\">Noch keine Aktivität aufgezeichnet.</div>"
                } else {
                        eventMarkup = events.map { entry in
                                let time = Self.shortTime(entry.TimestampUtc)
                                let kind = Self.escapeHTML(entry.Kind ?? "info")
                                let tool = Self.escapeHTML(entry.ToolName ?? "")
                                let message = Self.escapeHTML(entry.Message)
                                let toolMarkup = tool.isEmpty ? "" : "<span class=\"event-tool\">\(tool)</span>"
                                return "<div class=\"event-row\"><span class=\"event-time\">\(time)</span><span class=\"event-kind\">\(kind)</span>\(toolMarkup)<span class=\"event-message\">\(message)</span></div>"
                        }.joined()
                }

                let entryMarkup: String
                if entries.isEmpty {
                        entryMarkup = "<div class=\"empty\">Noch keine Tool-Aufrufe vorhanden.</div>"
                } else {
                        entryMarkup = entries.map { entry in
                                let entryId = Self.escapeHTML(entry.ToolCallId)
                                let openAttribute = expandedEntryIds.contains(entry.ToolCallId) ? " open" : ""
                                let title = Self.escapeHTML(entry.ToolName)
                                let status = Self.escapeHTML(Self.statusLabel(for: entry.Status))
                                let statusClass = Self.escapeHTML(entry.Status.lowercased())
                                let started = Self.escapeHTML(Self.formatDisplayTimestamp(entry.StartedUtc))
                                let completed = Self.escapeHTML(Self.formatDisplayTimestamp(entry.CompletedUtc))
                                let durationLine = entry.CompletedUtc == nil ? "Laufend" : "Abgeschlossen: \(completed)"
                                let request = Self.escapeHTML(entry.ArgumentsJson.isEmpty ? "{}" : entry.ArgumentsJson)
                                let result = Self.escapeHTML(entry.Result.isEmpty ? "Noch kein Ergebnis." : entry.Result)
                                let screenshotMarkup = buildScreenshotMarkup(for: entry)
                                let screenshotSummary = entry.Screenshots.isEmpty ? "Keine Screenshots" : "\(entry.Screenshots.count) Screenshot(s)"

                                return """
                                <details class=\"entry-card\" data-entry-id=\"\(entryId)\"\(openAttribute)>
                                    <summary>
                                        <div class=\"entry-heading\">
                                            <div>
                                                <div class=\"entry-title-row\">
                                                    <span class=\"entry-title\">\(title)</span>
                                                    <span class=\"status-badge \(statusClass)\">\(status)</span>
                                                </div>
                                                <div class=\"entry-meta\">Start: \(started) · \(Self.escapeHTML(durationLine)) · \(Self.escapeHTML(screenshotSummary))</div>
                                            </div>
                                        </div>

                                                    for (const workflowNode of document.querySelectorAll('[data-workflow-target]')) {
                                                        workflowNode.addEventListener('click', function () {
                                                            const entryId = workflowNode.getAttribute('data-workflow-target');
                                                            const details = document.querySelector('details[data-entry-id="' + entryId + '"]');
                                                            if (!details) {
                                                                return;
                                                            }

                                                            details.open = true;
                                                            details.scrollIntoView({ behavior: 'smooth', block: 'start' });

                                                            for (const node of document.querySelectorAll('.workflow-node')) {
                                                                node.classList.remove('active');
                                                            }

                                                            workflowNode.classList.add('active');
                                                        });
                                                    }
                                    </summary>
                                    <div class=\"entry-body\">
                                        <div class=\"section-label\">Request</div>
                                        <pre>\(request)</pre>
                                            <section class="section">
                                                <div class="section-header">Workflow</div>
                                                <div class="section-body workflow-shell">
                                                    \(workflowMarkup)
                                                </div>
                                            </section>
                                        <div class=\"section-label\">Result</div>
                                        <pre>\(result)</pre>
                                        \(screenshotMarkup)
                                    </div>
                                </details>
                                """
                        }.joined(separator: "\n")
                }

                return """
                <!doctype html>
                <html>
                <head>
                    <meta charset=\"utf-8\" />
                    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />
                    <style>
                        :root {
                            color-scheme: dark;
                            --bg: #171b22;
                            --panel: #1e2430;
                            --panel-2: #252c39;
                            --border: rgba(255,255,255,0.08);
                            --text: #eef2f7;
                            --muted: #9aabbe;
                            --accent: #75c7ec;
                            --success: #8fd19e;
                            --warning: #f2c572;
                            --danger: #f08a87;
                        }
                        html, body {
                            margin: 0;
                            padding: 0;
                            background: transparent;
                            color: var(--text);
                            font: 13px/1.5 -apple-system, BlinkMacSystemFont, sans-serif;
                        }
                        body {
                            padding: 8px 4px 24px;
                        }
                        .section {
                            margin-bottom: 16px;
                            border: 1px solid var(--border);
                            background: rgba(255,255,255,0.03);
                            border-radius: 14px;
                            overflow: hidden;
                        }
                        .section-header {
                            padding: 12px 14px;
                            font-size: 12px;
                            letter-spacing: 0.08em;
                            text-transform: uppercase;
                            color: var(--muted);
                            background: rgba(255,255,255,0.03);
                            border-bottom: 1px solid var(--border);
                        }
                        .section-body {
                            padding: 12px;
                        }
                        .workflow-shell {
                            padding: 14px 12px 16px;
                        }
                        .workflow-scroll {
                            overflow-x: auto;
                            overflow-y: hidden;
                            padding-bottom: 4px;
                        }
                        .workflow-track {
                            display: inline-flex;
                            align-items: center;
                            gap: 0;
                            min-width: 100%;
                        }
                        .workflow-anchor,
                        .workflow-node {
                            position: relative;
                            display: flex;
                            flex-direction: column;
                            gap: 6px;
                            min-width: 190px;
                            max-width: 190px;
                            padding: 14px 14px 12px;
                            border-radius: 16px;
                            border: 1px solid var(--border);
                            background: linear-gradient(180deg, rgba(255,255,255,0.05), rgba(255,255,255,0.02));
                            box-shadow: inset 0 1px 0 rgba(255,255,255,0.03);
                        }
                        .workflow-anchor {
                            min-width: 140px;
                            max-width: 140px;
                            background: linear-gradient(180deg, rgba(117,199,236,0.14), rgba(117,199,236,0.06));
                        }
                        .workflow-node {
                            cursor: pointer;
                            text-align: left;
                            color: inherit;
                        }
                        button.workflow-node {
                            appearance: none;
                            -webkit-appearance: none;
                            font: inherit;
                        }
                        .workflow-node.active {
                            border-color: rgba(117,199,236,0.45);
                            box-shadow: 0 0 0 1px rgba(117,199,236,0.24), 0 10px 30px rgba(0,0,0,0.18);
                        }
                        .workflow-node.running {
                            background: linear-gradient(180deg, rgba(242,197,114,0.16), rgba(255,255,255,0.03));
                        }
                        .workflow-node.completed {
                            background: linear-gradient(180deg, rgba(143,209,158,0.14), rgba(255,255,255,0.03));
                        }
                        .workflow-node.failed {
                            background: linear-gradient(180deg, rgba(240,138,135,0.16), rgba(255,255,255,0.03));
                        }
                        .workflow-connector {
                            position: relative;
                            width: 42px;
                            height: 2px;
                            flex: 0 0 42px;
                            background: linear-gradient(90deg, rgba(117,199,236,0.55), rgba(117,199,236,0.16));
                            margin: 0 8px;
                        }
                        .workflow-connector::after {
                            content: '';
                            position: absolute;
                            right: -1px;
                            top: 50%;
                            width: 8px;
                            height: 8px;
                            border-top: 2px solid rgba(117,199,236,0.55);
                            border-right: 2px solid rgba(117,199,236,0.55);
                            transform: translateY(-50%) rotate(45deg);
                        }
                        .workflow-step {
                            font-size: 11px;
                            letter-spacing: 0.08em;
                            text-transform: uppercase;
                            color: var(--accent);
                        }
                        .workflow-title {
                            font-size: 15px;
                            font-weight: 700;
                            line-height: 1.25;
                        }
                        .workflow-subtitle {
                            font-size: 12px;
                            color: var(--muted);
                        }
                        .workflow-mini-meta {
                            display: flex;
                            flex-wrap: wrap;
                            gap: 6px;
                            margin-top: 2px;
                        }
                        .workflow-pill {
                            padding: 2px 7px;
                            border-radius: 999px;
                            font-size: 11px;
                            border: 1px solid rgba(255,255,255,0.07);
                            color: var(--muted);
                            background: rgba(0,0,0,0.14);
                        }
                        .event-row {
                            display: flex;
                            gap: 10px;
                            align-items: baseline;
                            padding: 6px 0;
                            border-bottom: 1px solid rgba(255,255,255,0.04);
                        }
                        .event-row:last-child {
                            border-bottom: none;
                        }
                        .event-time {
                            color: var(--muted);
                            font-family: ui-monospace, SFMono-Regular, monospace;
                        }
                        .event-kind {
                            color: var(--accent);
                            font-family: ui-monospace, SFMono-Regular, monospace;
                        }
                        .event-tool {
                            color: #cbd6e2;
                            font-family: ui-monospace, SFMono-Regular, monospace;
                        }
                        .event-message {
                            flex: 1;
                        }
                        .entry-card {
                            border: 1px solid var(--border);
                            border-radius: 14px;
                            background: linear-gradient(180deg, rgba(255,255,255,0.04), rgba(255,255,255,0.02));
                            margin-bottom: 12px;
                            overflow: hidden;
                        }
                        .entry-card summary {
                            list-style: none;
                            cursor: pointer;
                            padding: 14px 16px;
                        }
                        .entry-card summary::-webkit-details-marker {
                            display: none;
                        }
                        .entry-title-row {
                            display: flex;
                            gap: 10px;
                            align-items: center;
                            flex-wrap: wrap;
                        }
                        .entry-title {
                            font-size: 15px;
                            font-weight: 700;
                        }
                        .entry-meta {
                            margin-top: 4px;
                            color: var(--muted);
                            font-size: 12px;
                        }
                        .status-badge {
                            padding: 2px 8px;
                            border-radius: 999px;
                            font-size: 11px;
                            letter-spacing: 0.04em;
                            text-transform: uppercase;
                            border: 1px solid var(--border);
                        }
                        .status-badge.running {
                            color: var(--warning);
                            background: rgba(242,197,114,0.12);
                        }
                        .status-badge.completed {
                            color: var(--success);
                            background: rgba(143,209,158,0.12);
                        }
                        .status-badge.failed {
                            color: var(--danger);
                            background: rgba(240,138,135,0.12);
                        }
                        .entry-body {
                            padding: 0 16px 16px;
                        }
                        .section-label {
                            margin: 10px 0 6px;
                            color: var(--accent);
                            font-size: 12px;
                            letter-spacing: 0.08em;
                            text-transform: uppercase;
                        }
                        pre {
                            margin: 0;
                            padding: 12px;
                            border-radius: 10px;
                            background: var(--bg);
                            border: 1px solid var(--border);
                            white-space: pre-wrap;
                            word-break: break-word;
                            font: 12px/1.5 ui-monospace, SFMono-Regular, monospace;
                            color: #dbe6f2;
                        }
                        .screenshot-grid {
                            display: grid;
                            grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
                            gap: 12px;
                            margin-top: 8px;
                        }
                        .shot-card {
                            border: 1px solid var(--border);
                            border-radius: 12px;
                            overflow: hidden;
                            background: rgba(255,255,255,0.03);
                        }
                        .shot-card img {
                            display: block;
                            width: 100%;
                            height: auto;
                            background: #0f131a;
                        }
                        .shot-body {
                            padding: 10px 12px 12px;
                        }
                        .shot-kind {
                            font-size: 11px;
                            letter-spacing: 0.08em;
                            text-transform: uppercase;
                            color: var(--accent);
                        }
                        .shot-summary {
                            margin-top: 6px;
                            font-size: 12px;
                            color: #dbe6f2;
                        }
                        .shot-meta {
                            margin-top: 6px;
                            color: var(--muted);
                            font-size: 11px;
                        }
                        a {
                            color: var(--accent);
                            text-decoration: none;
                        }
                        .empty {
                            padding: 12px;
                            color: var(--muted);
                        }
                    </style>
                    <script>
                        document.addEventListener('DOMContentLoaded', function () {
                            for (const details of document.querySelectorAll('details[data-entry-id]')) {
                                details.addEventListener('toggle', function () {
                                    const entryId = details.getAttribute('data-entry-id');
                                    window.webkit.messageHandlers.toggleEntry.postMessage({ id: entryId, open: details.open });
                                });
                            }
                        });
                    </script>
                </head>
                <body>
                    <section class=\"section\">
                        <div class=\"section-header\">Aktuelle Ereignisse</div>
                        <div class=\"section-body\">\(eventMarkup)</div>
                    </section>
                    <section class=\"section\">
                        <div class=\"section-header\">Tool-Aufrufe</div>
                        <div class=\"section-body\">\(entryMarkup)</div>
                    </section>
                </body>
                </html>
                """
        }

            private func buildWorkflowMarkup(for entries: [ToolLogEntry]) -> String {
                guard entries.isEmpty == false else {
                    return "<div class=\"empty\">Sobald Tools laufen, erscheint hier ein interaktiver Ablauf mit klickbaren Schritten.</div>"
                }

                var segments: [String] = []
                segments.append("""
                <div class=\"workflow-anchor\">
                    <div class=\"workflow-step\">Start</div>
                    <div class=\"workflow-title\">Anfrage aktiv</div>
                    <div class=\"workflow-subtitle\">Der Host zeichnet jeden Tool-Aufruf als einzelnen Schritt auf.</div>
                </div>
                """)

                for (index, entry) in entries.enumerated() {
                    let entryId = Self.escapeHTML(entry.ToolCallId)
                    let step = String(index + 1)
                    let title = Self.escapeHTML(entry.ToolName)
                    let statusText = Self.escapeHTML(Self.statusLabel(for: entry.Status))
                    let statusClass = Self.escapeHTML(entry.Status.lowercased())
                    let startTime = Self.escapeHTML(Self.shortTime(entry.StartedUtc))
                    let screenshotCount = entry.Screenshots.count
                    let screenshotLabel = screenshotCount == 1 ? "1 Screenshot" : "\(screenshotCount) Screenshots"
                    let resultPreview = Self.escapeHTML(Self.workflowPreview(for: entry.Result))

                    segments.append("<div class=\"workflow-connector\"></div>")
                    segments.append("""
                    <button class=\"workflow-node \(statusClass)\" type=\"button\" data-workflow-target=\"\(entryId)\">
                        <div class=\"workflow-step\">Schritt \(step)</div>
                        <div class=\"workflow-title\">\(title)</div>
                        <div class=\"workflow-subtitle\">\(resultPreview)</div>
                        <div class=\"workflow-mini-meta\">
                        <span class=\"workflow-pill\">\(statusText)</span>
                        <span class=\"workflow-pill\">\(startTime)</span>
                        <span class=\"workflow-pill\">\(Self.escapeHTML(screenshotLabel))</span>
                        </div>
                    </button>
                    """)
                }

                segments.append("<div class=\"workflow-connector\"></div>")
                segments.append("""
                <div class=\"workflow-anchor\">
                    <div class=\"workflow-step\">Ende</div>
                    <div class=\"workflow-title\">Detailansicht</div>
                    <div class=\"workflow-subtitle\">Klick auf einen Schritt öffnet unten Request, Resultat und Screenshots.</div>
                </div>
                """)

                return "<div class=\"workflow-scroll\"><div class=\"workflow-track\">\(segments.joined())</div></div>"
            }

        private func buildScreenshotMarkup(for entry: ToolLogEntry) -> String {
                guard entry.Screenshots.isEmpty == false else {
                        return ""
                }

                let screenshots = entry.Screenshots.map { artifact in
                        let imagePath = Self.escapeHTML(Self.urlEncodedPathComponent(artifact.ImageFileName))
                        let kind = Self.escapeHTML(artifact.Kind)
                        let summary = Self.escapeHTML(artifact.Summary)
                        let mediaType = Self.escapeHTML(artifact.MediaType ?? "")
                        let retention = Self.escapeHTML(artifact.RetentionStatus ?? "")
                        let similarity = Self.escapeHTML(artifact.Similarity ?? "")
                        let metaLink: String
                        if let metaFileName = artifact.MetaFileName, !metaFileName.isEmpty {
                                let metaPath = Self.escapeHTML(Self.urlEncodedPathComponent(metaFileName))
                                metaLink = " · <a href=\"\(metaPath)\">Meta öffnen</a>"
                        } else {
                                metaLink = ""
                        }

                        var metaParts: [String] = []
                        if !mediaType.isEmpty {
                                metaParts.append(mediaType)
                        }
                        if !retention.isEmpty {
                                metaParts.append(retention)
                        }
                        if !similarity.isEmpty {
                                metaParts.append("Ähnlichkeit \(similarity)")
                        }

                        let metaLine = metaParts.isEmpty ? "" : "<div class=\"shot-meta\">\(metaParts.joined(separator: " · "))</div>"

                        return """
                        <div class=\"shot-card\">
                            <a href=\"\(imagePath)\"><img src=\"\(imagePath)\" alt=\"\(kind)\" /></a>
                            <div class=\"shot-body\">
                                <div class=\"shot-kind\">\(kind)</div>
                                <div class=\"shot-summary\">\(summary)</div>
                                \(metaLine)
                                <div class=\"shot-meta\"><a href=\"\(imagePath)\">Screenshot öffnen</a>\(metaLink)</div>
                            </div>
                        </div>
                        """
                }.joined(separator: "\n")

                return "<div class=\"section-label\">Screenshots</div><div class=\"screenshot-grid\">\(screenshots)</div>"
        }

        private static func shortTime(_ value: String?) -> String {
                guard let value else {
                        return "--:--:--"
                }

                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                if let date = formatter.date(from: value) {
                        let outputFormatter = DateFormatter()
                        outputFormatter.dateFormat = "HH:mm:ss"
                        return outputFormatter.string(from: date)
                }

                if value.count >= 19 {
                        let start = value.index(value.startIndex, offsetBy: 11)
                        let end = value.index(start, offsetBy: 8, limitedBy: value.endIndex) ?? value.endIndex
                        return String(value[start..<end])
                }

                return value
        }

        private static func formatDisplayTimestamp(_ value: String?) -> String {
                guard let value, !value.isEmpty else {
                        return "-"
                }

                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                if let date = formatter.date(from: value) {
                        let outputFormatter = DateFormatter()
                        outputFormatter.dateStyle = .short
                        outputFormatter.timeStyle = .medium
                        return outputFormatter.string(from: date)
                }

                return value
        }

        private static func statusLabel(for status: String) -> String {
                switch status.lowercased() {
                case "running":
                        return "laufend"
                case "failed":
                        return "fehler"
                default:
                        return "fertig"
                }
        }

                private static func workflowPreview(for result: String) -> String {
                    let trimmed = result.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard trimmed.isEmpty == false else {
                        return "Noch kein Ergebnis vorhanden."
                    }

                    let singleLine = trimmed.replacingOccurrences(of: "\n", with: " ")
                    if singleLine.count <= 96 {
                        return singleLine
                    }

                    let index = singleLine.index(singleLine.startIndex, offsetBy: 96)
                    return String(singleLine[..<index]) + "..."
                }

        private static func urlEncodedPathComponent(_ value: String) -> String {
                value.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? value
        }

        private static func escapeHTML(_ value: String) -> String {
                var escaped = value
                escaped = escaped.replacingOccurrences(of: "&", with: "&amp;")
                escaped = escaped.replacingOccurrences(of: "<", with: "&lt;")
                escaped = escaped.replacingOccurrences(of: ">", with: "&gt;")
                escaped = escaped.replacingOccurrences(of: "\"", with: "&quot;")
                return escaped
        }

        func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
                guard message.name == "toggleEntry",
                            let body = message.body as? [String: Any],
                            let entryId = body["id"] as? String,
                            let open = body["open"] as? Bool else {
                        return
                }

                if open {
                        expandedEntryIds.insert(entryId)
                } else {
                        expandedEntryIds.remove(entryId)
                }
        }

        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
                if navigationAction.navigationType == .linkActivated,
                     let url = navigationAction.request.url,
                     url.isFileURL {
                        NSWorkspace.shared.open(url)
                        decisionHandler(.cancel)
                        return
                }

                decisionHandler(.allow)
    }
}

final class StatusBarViewController: NSViewController, NSTextViewDelegate {
    private static let minimumLiveAudioCommitBytes = 4_800
    private static let panelWidth: CGFloat = 460
    private static let minimumPanelHeight: CGFloat = 216
    private static let maximumPanelHeight: CGFloat = 316
    private static let backgroundInset: CGFloat = 8
    private static let sideInset: CGFloat = 24
    private static let textHorizontalInset: CGFloat = 16
    private static let topInset: CGFloat = 28
    private static let bottomInset: CGFloat = 10
    private static let defaultTextHeight: CGFloat = 36
    private static let maximumTextHeight: CGFloat = 108
    private static let maximumStatusLength = 240
    private static let primaryTextColor = NSColor(calibratedRed: 0.12, green: 0.14, blue: 0.17, alpha: 0.96)
    private static let secondaryTextColor = NSColor(calibratedRed: 0.25, green: 0.29, blue: 0.35, alpha: 0.92)
    private static let accentTextColor = NSColor(calibratedRed: 0.09, green: 0.38, blue: 0.60, alpha: 0.98)
    private static let activityFilePath: String = {
        if let path = ProcessInfo.processInfo.environment["AIDESK_MENU_BAR_ACTIVITY_FILE"], !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return path
        }

        return URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("AIDeskAssistant", isDirectory: true)
            .appendingPathComponent("menu-bar-activity.json")
            .path
    }()

    private let serverURL: URL
    private let dismissPopover: () -> Void
    private let setActivity: (Bool) -> Void
    private let toggleLogWindow: () -> Void
    private let diagnosticsLogger: StatusBarDiagnosticsLogger
    private let backgroundView = NSVisualEffectView(frame: .zero)
    private let voicePopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let thinkingPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let textScrollView = NSScrollView(frame: .zero)
    private let textView = NSTextView(frame: .zero)
    private let statusLabel = NSTextField(labelWithString: "")
    private let toolStatusLabel = NSTextField(labelWithString: "Tool: -")
    private let usageLabel = NSTextField(labelWithString: "Input: - | Output: - | Total: -")
    private let logButton = NSButton(frame: .zero)
    private let activityIndicator = NSProgressIndicator(frame: .zero)
    private let recordButton = NSButton(frame: .zero)
    private let cancelButton = NSButton(title: "Cancel", target: nil, action: nil)
    private let quitSeparator = NSBox(frame: .zero)
    private let quitButton = NSButton(title: "⏻", target: nil, action: nil)
    private var currentPanelHeight: CGFloat = minimumPanelHeight

    private var audioPlayer: AVAudioPlayer?
    private var responseTask: Task<Void, Never>?
    private let audioEngine = AVAudioEngine()
    private let audioPlayerNode = AVAudioPlayerNode()
    private let playbackFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000, channels: 1, interleaved: false)!
    private let captureEngine = AVAudioEngine()
    private let captureFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000, channels: 1, interleaved: true)!
    private let captureQueue = DispatchQueue(label: "aidesk.statusbar.capture")
    private let followUpNoSpeechTimeout: TimeInterval = 4.0
    private let silenceCommitInterval: TimeInterval
    private let silenceAmplitudeThreshold: Double
    private let minimumRmsThreshold: Double
    private let minimumSpeechDuration: TimeInterval
    private let rmsCalibrationDuration: TimeInterval
    private let noiseFloorMultiplier: Double
    private let noiseFloorPadding: Double
    private var captureConverter: AVAudioConverter?
    private var liveAudioSessionId: String?
    private var autoCommitMonitorTask: Task<Void, Never>?
    private var lastSpeechAt: Date?
    private var recordingStartedAt: Date?
    private var hasDetectedSpeech = false
    private var calibratedNoiseFloorRms = 0.0
    private var calibrationSampleCount = 0
    private var captureChunkCounter = 0
    private var uploadedChunkCounter = 0
    private var uploadedByteCount = 0
    private var playbackEngineConfigured = false
    private var accumulatedResponseText = ""
    private var availableVoices: [String] = []
    private var availableThinkingLevels: [String] = []
    private var isUpdatingVoiceSelection = false
    private var isUpdatingThinkingSelection = false
    private var isBusy = false
    private var activityPollTimer: Timer?
    private var pendingPlaybackChunkCount = 0
    private var pendingPlaybackDurationSeconds = 0.0
    private var currentResponseAllowsAutoFollowUpRecording = false
    private var currentResponseReceivedAudio = false
    private var shouldAutoResumeRecordingAfterPlayback = false
    private var isAutoFollowUpRecording = false
    private var autoResumeAfterPlaybackTask: Task<Void, Never>?
    private lazy var hostSession: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 600
        configuration.timeoutIntervalForResource = 3_600
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    init(serverURL: URL, dismissPopover: @escaping () -> Void, setActivity: @escaping (Bool) -> Void, toggleLogWindow: @escaping () -> Void, diagnosticsLogger: StatusBarDiagnosticsLogger) {
        self.serverURL = serverURL
        self.dismissPopover = dismissPopover
        self.setActivity = setActivity
        self.toggleLogWindow = toggleLogWindow
        self.diagnosticsLogger = diagnosticsLogger
        let environment = ProcessInfo.processInfo.environment
        self.silenceCommitInterval = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_SILENCE_COMMIT_SECONDS"], fallback: 0.9)
        self.silenceAmplitudeThreshold = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_SILENCE_THRESHOLD"], fallback: 0.015)
        self.minimumRmsThreshold = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_RMS_THRESHOLD"], fallback: 0.003)
        self.minimumSpeechDuration = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_MIN_SPEECH_SECONDS"], fallback: 0.25)
        self.rmsCalibrationDuration = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_RMS_CALIBRATION_SECONDS"], fallback: 0.35)
        self.noiseFloorMultiplier = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_NOISE_FLOOR_MULTIPLIER"], fallback: 2.5)
        self.noiseFloorPadding = StatusBarViewController.readPositiveDouble(environment["AIDESK_MENU_BAR_NOISE_FLOOR_PADDING"], fallback: 0.002)
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    deinit {
        activityPollTimer?.invalidate()
        hostSession.invalidateAndCancel()
    }

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: Self.panelWidth, height: Self.minimumPanelHeight))
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor.clear.cgColor

        backgroundView.frame = view.bounds.insetBy(dx: Self.backgroundInset, dy: Self.backgroundInset)
        backgroundView.autoresizingMask = [.width, .height]
        backgroundView.material = .popover
        backgroundView.blendingMode = .withinWindow
        backgroundView.state = .active
        backgroundView.wantsLayer = true
        backgroundView.layer?.cornerRadius = 24
        backgroundView.layer?.masksToBounds = true
        backgroundView.layer?.borderWidth = 1
        backgroundView.layer?.borderColor = NSColor.black.withAlphaComponent(0.10).cgColor
        view.addSubview(backgroundView)

        voicePopup.target = self
        voicePopup.action = #selector(changeVoice(_:))
        voicePopup.bezelStyle = .rounded
        voicePopup.contentTintColor = Self.primaryTextColor

        thinkingPopup.target = self
        thinkingPopup.action = #selector(changeThinking(_:))
        thinkingPopup.bezelStyle = .rounded
        thinkingPopup.contentTintColor = Self.primaryTextColor
        thinkingPopup.toolTip = "Thinking-Level fuer unterstuetzte Chat-Modelle"

        textScrollView.borderType = .noBorder
        textScrollView.drawsBackground = false
        textScrollView.hasVerticalScroller = false
        textScrollView.hasHorizontalScroller = false
        textScrollView.autohidesScrollers = true

        textView.isRichText = false
        textView.importsGraphics = false
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticDataDetectionEnabled = false
        textView.isAutomaticLinkDetectionEnabled = false
        textView.isContinuousSpellCheckingEnabled = false
        textView.allowsUndo = true
        textView.isHorizontallyResizable = false
        textView.isVerticallyResizable = true
        textView.minSize = NSSize(width: 0, height: Self.defaultTextHeight)
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.containerSize = NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainerInset = NSSize(width: 0, height: 7)
        textView.font = NSFont.systemFont(ofSize: 21, weight: .regular)
        textView.textColor = Self.primaryTextColor
        textView.backgroundColor = .clear
        textView.insertionPointColor = Self.primaryTextColor
        textView.delegate = self
        textScrollView.documentView = textView

        statusLabel.lineBreakMode = .byWordWrapping
        statusLabel.maximumNumberOfLines = 1
        statusLabel.font = NSFont.systemFont(ofSize: 14, weight: .medium)
        statusLabel.textColor = Self.accentTextColor
        statusLabel.isHidden = true

        toolStatusLabel.lineBreakMode = .byTruncatingTail
        toolStatusLabel.maximumNumberOfLines = 1
        toolStatusLabel.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        toolStatusLabel.textColor = Self.secondaryTextColor
        toolStatusLabel.alignment = .left

        usageLabel.lineBreakMode = .byWordWrapping
        usageLabel.maximumNumberOfLines = 1
        usageLabel.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        usageLabel.textColor = Self.secondaryTextColor
        usageLabel.alignment = .left
        usageLabel.cell?.truncatesLastVisibleLine = true

        logButton.target = self
        logButton.action = #selector(toggleLogWindowAction)
        logButton.bezelStyle = .rounded
        logButton.image = NSImage(systemSymbolName: "list.bullet.rectangle.portrait", accessibilityDescription: "Tool-Log")
        logButton.imagePosition = .imageOnly
        logButton.contentTintColor = Self.primaryTextColor
        logButton.toolTip = "Tool-Log ein- oder ausblenden"

        activityIndicator.style = .spinning
        activityIndicator.controlSize = .small
        activityIndicator.isDisplayedWhenStopped = false

        recordButton.target = self
        recordButton.action = #selector(toggleRecording)
        recordButton.bezelStyle = .rounded
        recordButton.image = NSImage(systemSymbolName: "mic.fill", accessibilityDescription: "Aufnehmen")
        recordButton.imagePosition = .imageOnly
        recordButton.contentTintColor = Self.primaryTextColor
        recordButton.toolTip = "Aufnahme starten oder stoppen"

        cancelButton.target = self
        cancelButton.action = #selector(cancelCurrentWork)
        cancelButton.isEnabled = false
        cancelButton.bezelStyle = .rounded
        cancelButton.image = NSImage(systemSymbolName: "stop.fill", accessibilityDescription: "Abbrechen")
        cancelButton.imagePosition = .imageOnly
        cancelButton.contentTintColor = Self.primaryTextColor
        cancelButton.toolTip = "Laufende Antwort abbrechen"

        quitSeparator.boxType = .custom
        quitSeparator.borderType = .lineBorder
        quitSeparator.borderWidth = 1
        quitSeparator.cornerRadius = 0
        quitSeparator.borderColor = NSColor.white.withAlphaComponent(0.18)
        quitSeparator.fillColor = .clear

        quitButton.target = self
        quitButton.action = #selector(quitApp)
        quitButton.bezelStyle = .rounded
        quitButton.font = NSFont.systemFont(ofSize: 16, weight: .semibold)
        quitButton.contentTintColor = Self.primaryTextColor
        quitButton.toolTip = "AIDesk beenden"

        backgroundView.addSubview(voicePopup)
        backgroundView.addSubview(thinkingPopup)
        backgroundView.addSubview(textScrollView)
        backgroundView.addSubview(statusLabel)
        backgroundView.addSubview(toolStatusLabel)
        backgroundView.addSubview(usageLabel)
        backgroundView.addSubview(logButton)
        backgroundView.addSubview(activityIndicator)
        backgroundView.addSubview(recordButton)
        backgroundView.addSubview(cancelButton)
        backgroundView.addSubview(quitSeparator)
        backgroundView.addSubview(quitButton)

        configureRecordButton(isRecording: false)
        updateOverlayLayout(animated: false)
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        focusTextField()
        startActivityPolling()
        Task { [weak self] in
            await self?.loadVoiceSettings()
        }
    }

    override func viewDidDisappear() {
        super.viewDidDisappear()
        activityPollTimer?.invalidate()
        activityPollTimer = nil
    }

    func focusTextField() {
        view.window?.makeFirstResponder(textView)
        textView.setSelectedRange(NSRange(location: textView.string.count, length: 0))
        textView.scrollRangeToVisible(textView.selectedRange())
    }

    func runSelfTestIfConfigured() {
        let environment = ProcessInfo.processInfo.environment
        guard let text = environment["AIDESK_MENU_BAR_SELF_TEST_TEXT"]?.trimmingCharacters(in: .whitespacesAndNewlines),
              !text.isEmpty else {
            return
        }

        diagnosticsLogger.log("Self-test started")
        submitText(text)
    }

    func textDidChange(_ notification: Notification) {
        updateOverlayLayout(animated: true)
        scrollTextSelectionToVisible()
    }

    func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(insertNewline(_:)) {
            let modifierFlags = NSApp.currentEvent?.modifierFlags.intersection(.deviceIndependentFlagsMask) ?? []
            if modifierFlags.contains(.shift) || modifierFlags.contains(.option) {
                return false
            }

            submitCurrentText(dismissAfterSend: false)
            return true
        }

        if commandSelector == #selector(cancelOperation(_:)) {
            dismissPopover()
            return true
        }

        return false
    }

    @objc private func sendText(_ sender: Any?) {
        submitCurrentText(dismissAfterSend: false)
    }

    @objc private func toggleLogWindowAction(_ sender: Any?) {
        toggleLogWindow()
    }

    private func submitCurrentText(dismissAfterSend: Bool) {
        let text = textView.string.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            setStatus("")
            return
        }

        textView.string = ""
        updateOverlayLayout(animated: true)
        if dismissAfterSend {
            dismissPopover()
        } else {
            focusTextField()
        }
        submitText(text)
    }

    @objc private func changeVoice(_ sender: Any?) {
        if isUpdatingVoiceSelection {
            return
        }

        let index = voicePopup.indexOfSelectedItem
        guard index >= 0, index < availableVoices.count else {
            return
        }

        let selectedVoice = availableVoices[index]
        setStatus("Wechsle Stimme zu \(Self.displayName(forVoiceId: selectedVoice)) …")
        voicePopup.isEnabled = false

        Task { [weak self] in
            await self?.submitVoiceSelection(selectedVoice)
        }
    }

    @objc private func changeThinking(_ sender: Any?) {
        if isUpdatingThinkingSelection {
            return
        }

        let index = thinkingPopup.indexOfSelectedItem
        guard index >= 0, index < availableThinkingLevels.count else {
            return
        }

        let selectedThinkingLevel = availableThinkingLevels[index]
        setStatus("Thinking-Level wechselt zu \(Self.displayName(forThinkingLevel: selectedThinkingLevel)) ...")
        thinkingPopup.isEnabled = false

        Task { [weak self] in
            await self?.submitThinkingSelection(selectedThinkingLevel)
        }
    }

    @objc private func toggleRecording(_ sender: Any?) {
        if liveAudioSessionId != nil {
            stopRecordingAndSend(autoTriggered: false)
        } else {
            startRecording(autoFollowUp: false, interruptCurrentWork: true)
        }
    }

    @objc private func cancelCurrentWork(_ sender: Any?) {
        guard hasActiveWork() else {
            setStatus("Nichts zu stoppen")
            return
        }

        setStatus("Breche ab …")
        clearUsage()
        setBusy(true)

        Task { [weak self] in
            await self?.interruptCurrentResponse(sendRemoteCancel: true)
            await MainActor.run { [weak self] in
                self?.setBusy(false)
                self?.setStatus("Abgebrochen")
            }
        }
    }

    @objc private func quitApp(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    private func startRecording(autoFollowUp: Bool, interruptCurrentWork: Bool) {
        recordButton.isEnabled = false
        resetCurrentResponseAudioState()
        isAutoFollowUpRecording = autoFollowUp
        setStatus(autoFollowUp ? "Höre wieder zu …" : "Starte Live-Mikrofon …")
        clearUsage()
        setBusy(true)

        Task { [weak self] in
            if interruptCurrentWork {
                await self?.interruptCurrentResponse(sendRemoteCancel: true)
            }
            await MainActor.run { [weak self] in
                self?.setStatus(autoFollowUp ? "Auto-Aufnahme startet …" : "Starte Live-Mikrofon …")
            }
            await self?.beginLiveRecording()
        }
    }

    private func stopRecordingAndSend(autoTriggered: Bool) {
        stopCaptureEngine()
        drainCaptureQueue(reason: "before live audio commit")
        configureRecordButton(isRecording: false)
        recordButton.isEnabled = true

        guard let sessionId = liveAudioSessionId else {
            diagnosticsLogger.log("No live audio session to commit")
            setStatus("Keine laufende Mikrofonaufnahme")
            return
        }

        if uploadedByteCount < Self.minimumLiveAudioCommitBytes {
            diagnosticsLogger.log("Skipping live audio commit because only \(uploadedByteCount) bytes were uploaded")
            stopRecordingWithoutSending(sessionId: sessionId, reason: "Aufnahme zu kurz, bitte etwas länger sprechen.")
            return
        }

        liveAudioSessionId = nil
        isAutoFollowUpRecording = false

        setStatus(autoTriggered ? "Stille erkannt, verarbeite Live-Audio …" : "Verarbeite Live-Audio …")
        setBusy(true)
        diagnosticsLogger.log(autoTriggered ? "Auto-committing live audio after silence" : "Live recording stopped, committing streamed audio")

        var components = URLComponents(url: serverURL.appendingPathComponent("audio-live/commit-stream"), resolvingAgainstBaseURL: false)
        components?.queryItems = [URLQueryItem(name: "sessionId", value: sessionId)]

        guard let url = components?.url else {
            setStatus("Konnte Commit-URL nicht bilden")
            return
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data("{}".utf8)

        startStreamingRequest(request, sendRemoteCancel: false, allowAutoFollowUpAfterResponse: true)
    }

    private func beginLiveRecording() async {
        do {
            let sessionId = try await openLiveAudioSession()
            try startCaptureEngine(sessionId: sessionId)
            liveAudioSessionId = sessionId
            recordingStartedAt = Date()
            lastSpeechAt = nil
            hasDetectedSpeech = false
            calibratedNoiseFloorRms = 0
            calibrationSampleCount = 0
            captureChunkCounter = 0
            uploadedChunkCounter = 0
            uploadedByteCount = 0
            startAutoCommitMonitor(sessionId: sessionId)

            await MainActor.run { [weak self] in
                self?.configureRecordButton(isRecording: true, isAutoFollowUp: self?.isAutoFollowUpRecording ?? false)
                self?.recordButton.isEnabled = true
                self?.setStatus((self?.isAutoFollowUpRecording ?? false) ? "Auto-Aufnahme aktiv …" : "Nehme live auf … Stop ist optional.")
            }

            diagnosticsLogger.log("Live audio thresholds: peak=\(String(format: "%.4f", silenceAmplitudeThreshold)) rmsMin=\(String(format: "%.4f", minimumRmsThreshold)) silence=\(String(format: "%.2f", silenceCommitInterval))s calibration=\(String(format: "%.2f", rmsCalibrationDuration))s")
            diagnosticsLogger.log("Live recording started with session \(sessionId), autoFollowUp=\(isAutoFollowUpRecording)")
        } catch {
            diagnosticsLogger.log("Failed to start live recording: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.configureRecordButton(isRecording: false, isAutoFollowUp: false)
                self?.recordButton.isEnabled = true
                self?.setStatus((self?.isAutoFollowUpRecording ?? false) ? "Auto-Aufnahmefehler: \(error.localizedDescription)" : "Live-Aufnahmefehler: \(error.localizedDescription)")
            }
        }
    }

    private func startStreamingRequest(_ request: URLRequest, sendRemoteCancel: Bool = true, allowAutoFollowUpAfterResponse: Bool = false) {
        let previousResponseTask = responseTask
        clearUsage()
        setBusy(true)
        responseTask = Task { [weak self] in
            guard let self else {
                return
            }

            await self.interruptCurrentResponse(previousResponseTask: previousResponseTask, sendRemoteCancel: sendRemoteCancel)
            self.currentResponseAllowsAutoFollowUpRecording = allowAutoFollowUpAfterResponse
            self.currentResponseReceivedAudio = false
            self.shouldAutoResumeRecordingAfterPlayback = false
            self.diagnosticsLogger.log("Starting streaming request to \(request.url?.absoluteString ?? "<unknown>") autoFollowUpAfterResponse=\(allowAutoFollowUpAfterResponse)")
            await self.consumeStreamingResponse(request)
        }
    }

    private func submitText(_ text: String) {
        setStatus("Sende Nachricht …")
        diagnosticsLogger.log("Sending text stream: \(text)")

        var request = URLRequest(url: serverURL.appendingPathComponent("message-stream"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["text": text])

        startStreamingRequest(request)
    }

    private func interruptCurrentResponse(sendRemoteCancel: Bool) async {
        let currentResponseTask = responseTask
        responseTask = nil
        await interruptCurrentResponse(previousResponseTask: currentResponseTask, sendRemoteCancel: sendRemoteCancel)
    }

    private func interruptCurrentResponse(previousResponseTask: Task<Void, Never>?, sendRemoteCancel: Bool) async {
        let activeLiveAudioSessionId = liveAudioSessionId
        let hadActiveResponse = previousResponseTask != nil || audioPlayerNode.isPlaying || (audioPlayer?.isPlaying ?? false)
        previousResponseTask?.cancel()
        stopPlayback()
        resetCurrentResponseAudioState()
        isAutoFollowUpRecording = false
        accumulatedResponseText = ""

        if activeLiveAudioSessionId != nil {
            stopCaptureEngine()
            drainCaptureQueue(reason: "before live audio interruption")
            liveAudioSessionId = nil
            configureRecordButton(isRecording: false)
            recordButton.isEnabled = true
            diagnosticsLogger.log("Interrupting active live audio capture")
        }

        if hadActiveResponse {
            diagnosticsLogger.log("Interrupting active response")
        }

        if let activeLiveAudioSessionId, sendRemoteCancel {
            await sendLiveAudioCancelRequest(sessionId: activeLiveAudioSessionId)
        }

        if sendRemoteCancel {
            await sendCancelRequestIfNeeded(onlyWhenActive: hadActiveResponse)
        }

        if let previousResponseTask {
            _ = await previousResponseTask.result
        }
    }

    private func sendCancelRequestIfNeeded(onlyWhenActive: Bool) async {
        if onlyWhenActive == false {
            return
        }

        do {
            var request = URLRequest(url: serverURL.appendingPathComponent("cancel"))
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = Data("{}".utf8)
            let (_, response) = try await hostSession.data(for: request)
            if let httpResponse = response as? HTTPURLResponse {
                diagnosticsLogger.log("Cancel request returned HTTP \(httpResponse.statusCode)")
            } else {
                diagnosticsLogger.log("Cancel request completed")
            }
        } catch {
            diagnosticsLogger.log("Cancel request failed: \(error.localizedDescription)")
        }
    }

    private func sendLiveAudioCancelRequest(sessionId: String) async {
        do {
            var components = URLComponents(url: serverURL.appendingPathComponent("audio-live/cancel"), resolvingAgainstBaseURL: false)
            components?.queryItems = [URLQueryItem(name: "sessionId", value: sessionId)]
            guard let url = components?.url else {
                return
            }

            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = Data("{}".utf8)
            let (_, response) = try await hostSession.data(for: request)
            if let httpResponse = response as? HTTPURLResponse {
                diagnosticsLogger.log("Live audio cancel returned HTTP \(httpResponse.statusCode)")
            }
        } catch {
            diagnosticsLogger.log("Live audio cancel failed: \(error.localizedDescription)")
        }
    }

    private func openLiveAudioSession() async throws -> String {
        var request = URLRequest(url: serverURL.appendingPathComponent("audio-live/start"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data("{}".utf8)

        let (data, response) = try await hostSession.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode) else {
            let hostError = StatusBarViewController.extractHostErrorMessage(from: data) ?? "Der Live-Audio-Start wurde vom lokalen Host abgelehnt."
            throw NSError(domain: "AIDeskAssistant", code: 1, userInfo: [NSLocalizedDescriptionKey: hostError])
        }

        let startResponse = try JSONDecoder().decode(AudioLiveStartResponse.self, from: data)
        diagnosticsLogger.log("Opened live audio session \(startResponse.sessionId)")
        return startResponse.sessionId
    }

    private func startCaptureEngine(sessionId: String) throws {
        let inputNode = captureEngine.inputNode
        let inputFormat = inputNode.outputFormat(forBus: 0)
        captureConverter = AVAudioConverter(from: inputFormat, to: captureFormat)

        inputNode.removeTap(onBus: 0)
        inputNode.installTap(onBus: 0, bufferSize: 4096, format: inputFormat) { [weak self] buffer, _ in
            guard let self else {
                return
            }

            self.captureQueue.async {
                guard self.liveAudioSessionId == sessionId || self.liveAudioSessionId == nil else {
                    return
                }

                guard let chunk = self.makeLivePCMChunk(from: buffer) else {
                    self.diagnosticsLogger.log("Dropping live audio chunk because conversion failed")
                    return
                }

                self.observeSpeechLevel(for: chunk)

                self.sendLiveAudioChunk(chunk, sessionId: sessionId)
            }
        }

        captureEngine.prepare()
        try captureEngine.start()
    }

    private func stopCaptureEngine() {
        autoCommitMonitorTask?.cancel()
        autoCommitMonitorTask = nil
        let inputNode = captureEngine.inputNode
        inputNode.removeTap(onBus: 0)
        if captureEngine.isRunning {
            captureEngine.stop()
        }
        recordingStartedAt = nil
        lastSpeechAt = nil
        hasDetectedSpeech = false
        isAutoFollowUpRecording = false
        diagnosticsLogger.log("Live capture engine stopped")
    }

    private func startAutoCommitMonitor(sessionId: String) {
        autoCommitMonitorTask?.cancel()
        autoCommitMonitorTask = Task { [weak self] in
            guard let self else {
                return
            }

            while Task.isCancelled == false {
                try? await Task.sleep(nanoseconds: 150_000_000)

                guard self.liveAudioSessionId == sessionId,
                      let recordingStartedAt = self.recordingStartedAt else {
                    continue
                }

                if self.hasDetectedSpeech == false {
                    if self.isAutoFollowUpRecording {
                        let idleDuration = Date().timeIntervalSince(recordingStartedAt)
                        if idleDuration >= self.followUpNoSpeechTimeout {
                            self.diagnosticsLogger.log("Auto follow-up recording stopped after \(String(format: "%.2f", idleDuration))s without speech")
                            await MainActor.run { [weak self] in
                                guard let self, self.liveAudioSessionId == sessionId else {
                                    return
                                }

                                self.stopRecordingWithoutSending(sessionId: sessionId, reason: "Keine Sprache erkannt")
                            }
                            return
                        }
                    }

                    continue
                }

                guard let lastSpeechAt = self.lastSpeechAt else {
                    continue
                }

                let silenceDuration = Date().timeIntervalSince(lastSpeechAt)
                let speechDuration = lastSpeechAt.timeIntervalSince(recordingStartedAt)
                if silenceDuration >= self.silenceCommitInterval && speechDuration >= self.minimumSpeechDuration {
                    self.diagnosticsLogger.log("Silence auto-commit triggered after \(String(format: "%.2f", silenceDuration))s, speechDuration=\(String(format: "%.2f", speechDuration))s")
                    await MainActor.run { [weak self] in
                        guard let self, self.liveAudioSessionId == sessionId else {
                            return
                        }

                        self.stopRecordingAndSend(autoTriggered: true)
                    }
                    return
                }
            }
        }
    }

    private func observeSpeechLevel(for chunk: Data) {
        guard let metrics = audioLevelMetrics(for: chunk) else {
            return
        }

        captureChunkCounter += 1
        let effectiveRmsThreshold = currentEffectiveRmsThreshold()
        let effectivePeakThreshold = max(silenceAmplitudeThreshold, effectiveRmsThreshold * 3.0)
        maybeUpdateNoiseFloorCalibration(with: metrics)

        if captureChunkCounter == 1 || captureChunkCounter.isMultiple(of: 6) {
            diagnosticsLogger.log("Mic levels: peak=\(String(format: "%.4f", metrics.peak)) rms=\(String(format: "%.4f", metrics.rms)) rmsThreshold=\(String(format: "%.4f", effectiveRmsThreshold)) peakThreshold=\(String(format: "%.4f", effectivePeakThreshold)) noiseFloor=\(String(format: "%.4f", calibratedNoiseFloorRms))")
        }

        if metrics.peak >= effectivePeakThreshold || metrics.rms >= effectiveRmsThreshold {
            let now = Date()
            lastSpeechAt = now
            if hasDetectedSpeech == false {
                hasDetectedSpeech = true
                diagnosticsLogger.log("Speech detected: peak=\(String(format: "%.4f", metrics.peak)) rms=\(String(format: "%.4f", metrics.rms))")
            }
        }
    }

    private func maybeUpdateNoiseFloorCalibration(with metrics: AudioLevelMetrics) {
        guard hasDetectedSpeech == false,
              let recordingStartedAt,
              Date().timeIntervalSince(recordingStartedAt) <= rmsCalibrationDuration else {
            return
        }

        calibrationSampleCount += 1
        if calibrationSampleCount == 1 {
            calibratedNoiseFloorRms = metrics.rms
        } else {
            let previousWeight = Double(calibrationSampleCount - 1)
            calibratedNoiseFloorRms = ((calibratedNoiseFloorRms * previousWeight) + metrics.rms) / Double(calibrationSampleCount)
        }

        if calibrationSampleCount <= 3 || calibrationSampleCount.isMultiple(of: 5) {
            diagnosticsLogger.log("Noise calibration: sample=\(calibrationSampleCount) rms=\(String(format: "%.4f", metrics.rms)) baseline=\(String(format: "%.4f", calibratedNoiseFloorRms))")
        }
    }

    private func currentEffectiveRmsThreshold() -> Double {
        let adaptiveThreshold = calibratedNoiseFloorRms > 0
            ? (calibratedNoiseFloorRms * noiseFloorMultiplier) + noiseFloorPadding
            : minimumRmsThreshold
        return max(minimumRmsThreshold, adaptiveThreshold)
    }

    private func audioLevelMetrics(for chunk: Data) -> AudioLevelMetrics? {
        guard chunk.isEmpty == false else {
            return nil
        }

        let sampleCount = chunk.count / MemoryLayout<Int16>.size
        guard sampleCount > 0 else {
            return nil
        }

        var peak: Double = 0
        var sumSquares: Double = 0
        chunk.withUnsafeBytes { rawBuffer in
            guard let samples = rawBuffer.bindMemory(to: Int16.self).baseAddress else {
                return
            }

            for index in 0..<sampleCount {
                let normalized = Double(samples[index]) / Double(Int16.max)
                peak = max(peak, abs(normalized))
                sumSquares += normalized * normalized
            }
        }

        return AudioLevelMetrics(peak: peak, rms: sqrt(sumSquares / Double(sampleCount)))
    }

    private func makeLivePCMChunk(from buffer: AVAudioPCMBuffer) -> Data? {
        guard let captureConverter else {
            return nil
        }

        let ratio = captureFormat.sampleRate / buffer.format.sampleRate
        let estimatedCapacity = max(1, Int(Double(buffer.frameLength) * ratio) + 1024)

        guard let convertedBuffer = AVAudioPCMBuffer(pcmFormat: captureFormat, frameCapacity: AVAudioFrameCount(estimatedCapacity)) else {
            return nil
        }

        var consumed = false
        var conversionError: NSError?
        let status = captureConverter.convert(to: convertedBuffer, error: &conversionError) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }

            consumed = true
            outStatus.pointee = .haveData
            return buffer
        }

        if status == .error || conversionError != nil || convertedBuffer.frameLength == 0 {
            return nil
        }

        let audioBuffer = convertedBuffer.audioBufferList.pointee.mBuffers
        guard let bytes = audioBuffer.mData else {
            return nil
        }

        return Data(bytes: bytes, count: Int(audioBuffer.mDataByteSize))
    }

    private func sendLiveAudioChunk(_ chunk: Data, sessionId: String) {
        var components = URLComponents(url: serverURL.appendingPathComponent("audio-live/chunk"), resolvingAgainstBaseURL: false)
        components?.queryItems = [URLQueryItem(name: "sessionId", value: sessionId)]
        guard let url = components?.url else {
            return
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.httpBody = chunk

        let semaphore = DispatchSemaphore(value: 0)
        hostSession.dataTask(with: request) { [weak self] _, response, error in
            if let error {
                self?.diagnosticsLogger.log("Live audio chunk upload failed: \(error.localizedDescription)")
            } else if let httpResponse = response as? HTTPURLResponse, !(200...299).contains(httpResponse.statusCode) {
                self?.diagnosticsLogger.log("Live audio chunk upload returned HTTP \(httpResponse.statusCode)")
            } else {
                self?.uploadedChunkCounter += 1
                self?.uploadedByteCount += chunk.count
                if let self, self.uploadedChunkCounter == 1 || self.uploadedChunkCounter.isMultiple(of: 8) {
                    self.diagnosticsLogger.log("Uploaded live audio chunks: count=\(self.uploadedChunkCounter) bytes=\(self.uploadedByteCount)")
                }
            }
            semaphore.signal()
        }.resume()
        semaphore.wait()
    }

    private func drainCaptureQueue(reason: String) {
        captureQueue.sync { }
        diagnosticsLogger.log("Capture queue drained \(reason): uploadedChunks=\(uploadedChunkCounter) uploadedBytes=\(uploadedByteCount)")
    }

    private func consumeStreamingResponse(_ request: URLRequest) async {
        do {
            let (bytes, response) = try await hostSession.bytes(for: request)
            if let httpResponse = response as? HTTPURLResponse, !(200...299).contains(httpResponse.statusCode) {
                await MainActor.run { [weak self] in
                    self?.setStatus("Serverfehler: \(httpResponse.statusCode)")
                    self?.setBusy(false)
                }
                diagnosticsLogger.log("Streaming request failed with HTTP \(httpResponse.statusCode)")
                return
            }

            for try await line in bytes.lines {
                if Task.isCancelled {
                    diagnosticsLogger.log("Streaming request cancelled locally")
                    return
                }

                let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
                if trimmed.isEmpty {
                    continue
                }

                guard let data = trimmed.data(using: .utf8) else {
                    continue
                }

                let event = try JSONDecoder().decode(AssistantStreamEvent.self, from: data)
                await MainActor.run { [weak self] in
                    self?.present(event: event)
                }
            }

            await MainActor.run { [weak self] in
                self?.responseTask = nil
                self?.resumeRecordingAfterPlaybackIfPossible()
            }
        } catch is CancellationError {
            diagnosticsLogger.log("Streaming request cancelled")
            await MainActor.run { [weak self] in
                self?.responseTask = nil
                self?.setBusy(false)
            }
            return
        } catch {
            diagnosticsLogger.log("Streaming request error: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.responseTask = nil
                self?.resetCurrentResponseAudioState()
                self?.setStatus(self?.describeHostError(error, fallback: "Die Anfrage an den lokalen AIDesk-Host ist fehlgeschlagen") ?? "Die Anfrage an den lokalen AIDesk-Host ist fehlgeschlagen")
                self?.setBusy(false)
            }
        }
    }

    private func handleResponse(data: Data?, error: Error?) {
        if let error {
            DispatchQueue.main.async { [weak self] in
                self?.setStatus("Fehler: \(error.localizedDescription)")
            }
            return
        }

        guard let data else {
            DispatchQueue.main.async { [weak self] in
                self?.setStatus("Leere Antwort vom lokalen Assistenten")
            }
            return
        }

        do {
            let response = try JSONDecoder().decode(AssistantResponse.self, from: data)
            DispatchQueue.main.async { [weak self] in
                self?.present(response: response)
            }
        } catch {
            DispatchQueue.main.async { [weak self] in
                self?.setStatus("Antwort konnte nicht gelesen werden")
            }
        }
    }

    private func present(response: AssistantResponse) {
        if let error = response.error, !error.isEmpty {
            setStatus("Fehler: \(error)")
            setBusy(false)
            return
        }

        let spokenOrText = response.text?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let spokenOrText, !spokenOrText.isEmpty {
            setStatus(spokenOrText)
        } else {
            setStatus("Antwort empfangen")
        }

        presentUsage(response.usage)
        setBusy(false)

        guard let audioBase64 = response.audioBase64, let audioData = Data(base64Encoded: audioBase64) else {
            return
        }

        let audioURL = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("aidesk-response.wav")

        do {
            try audioData.write(to: audioURL, options: .atomic)
            audioPlayer = try AVAudioPlayer(contentsOf: audioURL)
            audioPlayer?.prepareToPlay()
            audioPlayer?.play()
        } catch {
            setStatus("Audio konnte nicht abgespielt werden")
        }
    }

    private func present(event: AssistantStreamEvent) {
        switch event.type {
        case "text_delta":
            if let text = event.text, !text.isEmpty {
                accumulatedResponseText += text
                diagnosticsLogger.log("Received text delta: \(text)")
                setStatus(accumulatedResponseText)
            }

        case "audio_delta":
            guard let pcmBase64 = event.pcmBase64, let pcmData = Data(base64Encoded: pcmBase64) else {
                return
            }

            do {
                currentResponseReceivedAudio = true
                pendingPlaybackDurationSeconds += Double(pcmData.count) / Double(24_000 * MemoryLayout<Int16>.size)
                try enqueuePCMChunk(pcmData)
                diagnosticsLogger.log("Received audio delta: \(pcmData.count) bytes")
            } catch {
                diagnosticsLogger.log("Audio streaming failed: \(error.localizedDescription)")
                setStatus("Audio konnte nicht gestreamt werden")
            }

        case "completed":
            diagnosticsLogger.log("Received completed event")
            if let text = event.text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
                accumulatedResponseText = text
                setStatus(text)
            } else if accumulatedResponseText.isEmpty {
                setStatus("Antwort empfangen")
            }
            presentUsage(event.usage)
            setBusy(false)
            responseTask = nil
            shouldAutoResumeRecordingAfterPlayback = currentResponseAllowsAutoFollowUpRecording && currentResponseReceivedAudio
            if shouldAutoResumeRecordingAfterPlayback {
                setStatus("Antwort fertig. Auto-Aufnahme folgt …")
                scheduleAutoResumeRecordingAfterPlayback()
            } else {
                resetCurrentResponseAudioState()
            }

        case "error":
            diagnosticsLogger.log("Received error event: \(event.error ?? "<none>")")
            if let error = event.error, !error.isEmpty {
                setStatus("Fehler: \(error)")
            } else {
                setStatus("Realtime-Stream fehlgeschlagen")
            }
            resetCurrentResponseAudioState()
            setBusy(false)

        case "cancelled":
            diagnosticsLogger.log("Received cancelled event")
            if accumulatedResponseText.isEmpty {
                setStatus("Antwort abgebrochen")
            }
            clearUsage()
            resetCurrentResponseAudioState()
            setBusy(false)

        default:
            return
        }
    }

    private func enqueuePCMChunk(_ data: Data) throws {
        guard !data.isEmpty else {
            return
        }

        try ensurePlaybackEngine()

        let frameCount = AVAudioFrameCount(data.count / MemoryLayout<Int16>.size)
        guard frameCount > 0,
              let buffer = AVAudioPCMBuffer(pcmFormat: playbackFormat, frameCapacity: frameCount),
              let channelData = buffer.int16ChannelData else {
            return
        }

        buffer.frameLength = frameCount
        data.withUnsafeBytes { rawBuffer in
            guard let source = rawBuffer.bindMemory(to: Int16.self).baseAddress else {
                return
            }

            channelData[0].update(from: source, count: Int(frameCount))
        }

        pendingPlaybackChunkCount += 1
        audioPlayerNode.scheduleBuffer(buffer) { [weak self] in
            DispatchQueue.main.async {
                self?.handlePlaybackChunkCompleted()
            }
        }
        if !audioPlayerNode.isPlaying {
            audioPlayerNode.play()
            diagnosticsLogger.log("PCM playback started")
        }
    }

    private func ensurePlaybackEngine() throws {
        if !playbackEngineConfigured {
            audioEngine.attach(audioPlayerNode)
            audioEngine.connect(audioPlayerNode, to: audioEngine.mainMixerNode, format: playbackFormat)
            playbackEngineConfigured = true
        }

        if !audioEngine.isRunning {
            try audioEngine.start()
            diagnosticsLogger.log("Audio engine started")
        }

        if !audioPlayerNode.isPlaying {
            audioPlayerNode.play()
        }
    }

    private func stopPlayback() {
        audioPlayer?.stop()
        audioPlayer = nil
        audioPlayerNode.stop()
        pendingPlaybackChunkCount = 0
        pendingPlaybackDurationSeconds = 0
        autoResumeAfterPlaybackTask?.cancel()
        autoResumeAfterPlaybackTask = nil
        if audioEngine.isRunning {
            audioEngine.stop()
        }
        diagnosticsLogger.log("Playback stopped")
    }

    private func stopRecordingWithoutSending(sessionId: String, reason: String) {
        stopCaptureEngine()
        drainCaptureQueue(reason: "without sending follow-up audio")
        liveAudioSessionId = nil
        configureRecordButton(isRecording: false)
        recordButton.isEnabled = true
        clearUsage()
        setBusy(false)
        setStatus(reason)

        Task { [weak self] in
            await self?.sendLiveAudioCancelRequest(sessionId: sessionId)
        }
    }

    private func handlePlaybackChunkCompleted() {
        pendingPlaybackChunkCount = max(0, pendingPlaybackChunkCount - 1)
        if pendingPlaybackChunkCount == 0 {
            audioPlayerNode.stop()
            if audioEngine.isRunning {
                audioEngine.stop()
            }
            diagnosticsLogger.log("PCM playback finished")
            if shouldAutoResumeRecordingAfterPlayback {
                scheduleAutoResumeRecordingAfterPlayback(delaySeconds: 0.05)
            }
        }
    }

    private func scheduleAutoResumeRecordingAfterPlayback(delaySeconds: TimeInterval? = nil) {
        autoResumeAfterPlaybackTask?.cancel()
        let effectiveDelaySeconds = delaySeconds ?? max(0.35, pendingPlaybackDurationSeconds + 0.2)
        let delayNanoseconds = UInt64(effectiveDelaySeconds * 1_000_000_000)
        diagnosticsLogger.log("Scheduling auto-resume recording after \(String(format: "%.2f", effectiveDelaySeconds))s, playbackChunks=\(pendingPlaybackChunkCount), audioPlayerNodePlaying=\(audioPlayerNode.isPlaying), audioPlayerPlaying=\(audioPlayer?.isPlaying ?? false)")

        autoResumeAfterPlaybackTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: delayNanoseconds)
            await MainActor.run { [weak self] in
                self?.resumeRecordingAfterPlaybackIfPossible()
            }
        }
    }

    private func resumeRecordingAfterPlaybackIfPossible() {
        guard shouldAutoResumeRecordingAfterPlayback,
              currentResponseAllowsAutoFollowUpRecording,
              liveAudioSessionId == nil else {
            diagnosticsLogger.log("Auto-resume skipped: should=\(shouldAutoResumeRecordingAfterPlayback) allows=\(currentResponseAllowsAutoFollowUpRecording) liveSession=\(liveAudioSessionId ?? "<none>")")
            return
        }

        if audioPlayerNode.isPlaying || (audioPlayer?.isPlaying ?? false) {
            diagnosticsLogger.log("Auto-resume delayed: audioPlayerNodePlaying=\(audioPlayerNode.isPlaying) audioPlayerPlaying=\(audioPlayer?.isPlaying ?? false)")
            scheduleAutoResumeRecordingAfterPlayback(delaySeconds: 0.2)
            return
        }

        if pendingPlaybackChunkCount > 0 {
            diagnosticsLogger.log("Ignoring stale playback chunk count \(pendingPlaybackChunkCount) and resuming recording")
            pendingPlaybackChunkCount = 0
        }

        diagnosticsLogger.log("Auto-resuming live recording after spoken response")
        setStatus("Auto-Aufnahme startet …")
        autoResumeAfterPlaybackTask?.cancel()
        autoResumeAfterPlaybackTask = nil
        resetCurrentResponseAudioState()
        startRecording(autoFollowUp: true, interruptCurrentWork: false)
    }

    private func resetCurrentResponseAudioState() {
        autoResumeAfterPlaybackTask?.cancel()
        autoResumeAfterPlaybackTask = nil
        pendingPlaybackDurationSeconds = 0
        currentResponseAllowsAutoFollowUpRecording = false
        currentResponseReceivedAudio = false
        shouldAutoResumeRecordingAfterPlayback = false
    }

    private func setStatus(_ text: String) {
        diagnosticsLogger.log("Status: \(text)")
        let displayText = Self.truncatedStatusText(text)
        statusLabel.stringValue = displayText
        statusLabel.isHidden = displayText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        updateOverlayLayout(animated: true)
    }

    private static func truncatedStatusText(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count > maximumStatusLength else {
            return text
        }

        let prefix = trimmed.prefix(maximumStatusLength - 1)
        return String(prefix) + "…"
    }

    private func updateOverlayLayout(animated: Bool) {
        let contentTextHeight = calculatedContentTextHeight()
        let textHeight = min(Self.maximumTextHeight, max(Self.defaultTextHeight, contentTextHeight))
        let desiredPanelHeight = min(Self.maximumPanelHeight, max(Self.minimumPanelHeight, textHeight + 138))
        currentPanelHeight = desiredPanelHeight

        view.frame = NSRect(x: 0, y: 0, width: Self.panelWidth, height: desiredPanelHeight)
        backgroundView.frame = view.bounds.insetBy(dx: Self.backgroundInset, dy: Self.backgroundInset)

        let contentHeight = backgroundView.bounds.height
        let contentWidth = backgroundView.bounds.width
        let topRowY = contentHeight - Self.topInset - 30
        let textY = topRowY - 12 - textHeight
        let statusY = textY - 28
        let bottomRowY = Self.bottomInset

        let voiceWidth: CGFloat = 98
        let thinkingWidth: CGFloat = 106
        voicePopup.frame = NSRect(x: Self.sideInset, y: topRowY, width: voiceWidth, height: 30)
        thinkingPopup.frame = NSRect(x: voicePopup.frame.maxX + 10, y: topRowY, width: thinkingWidth, height: 30)
        quitButton.frame = NSRect(x: contentWidth - Self.sideInset - 34, y: topRowY, width: 34, height: 30)
        quitSeparator.frame = NSRect(x: quitButton.frame.minX - 12, y: topRowY + 4, width: 1, height: 22)
        cancelButton.frame = NSRect(x: quitSeparator.frame.minX - 10 - 34, y: topRowY, width: 34, height: 30)
        recordButton.frame = NSRect(x: cancelButton.frame.minX - 6 - 34, y: topRowY, width: 34, height: 30)
        logButton.frame = NSRect(x: recordButton.frame.minX - 6 - 34, y: topRowY, width: 34, height: 30)

        textScrollView.frame = NSRect(x: Self.textHorizontalInset, y: textY, width: contentWidth - (Self.textHorizontalInset * 2), height: textHeight)
        textView.textContainer?.containerSize = NSSize(width: textScrollView.contentSize.width, height: CGFloat.greatestFiniteMagnitude)
        textView.frame = NSRect(origin: .zero, size: NSSize(width: textScrollView.contentSize.width, height: max(textHeight, contentTextHeight)))

        statusLabel.frame = NSRect(x: Self.sideInset, y: statusY, width: contentWidth - (Self.sideInset * 2) - 24, height: 22)
        toolStatusLabel.frame = NSRect(x: Self.sideInset, y: statusY - 18, width: contentWidth - (Self.sideInset * 2) - 24, height: 16)
        activityIndicator.frame = NSRect(x: contentWidth - Self.sideInset - 18, y: statusY + 2, width: 18, height: 18)
        usageLabel.frame = NSRect(x: Self.sideInset, y: bottomRowY + 8, width: contentWidth - (Self.sideInset * 2) - 160, height: 18)
        textScrollView.hasVerticalScroller = textHeight >= Self.maximumTextHeight - 0.5
        if textScrollView.hasVerticalScroller {
            scrollTextSelectionToVisible()
        }

        guard let window = view.window else {
            return
        }

        let resizeWindow = {
            let oldFrame = window.frame
            let newFrame = NSRect(x: oldFrame.origin.x, y: oldFrame.origin.y, width: Self.panelWidth, height: desiredPanelHeight)
            window.setFrame(newFrame, display: true)
        }

        if animated {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.12
                context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                resizeWindow()
            }
        } else {
            resizeWindow()
        }
    }

        private func calculatedContentTextHeight() -> CGFloat {
        guard let layoutManager = textView.layoutManager,
              let textContainer = textView.textContainer else {
            return Self.defaultTextHeight
        }

        layoutManager.ensureLayout(for: textContainer)
        let usedRect = layoutManager.usedRect(for: textContainer)
        let insetHeight = textView.textContainerInset.height * 2
        return ceil(usedRect.height + insetHeight)
    }

    private func scrollTextSelectionToVisible() {
        let selectedRange = textView.selectedRange()
        let rangeToReveal = selectedRange.length > 0
            ? selectedRange
            : NSRange(location: max(0, selectedRange.location - 1), length: min(1, textView.string.utf16.count))
        textView.scrollRangeToVisible(rangeToReveal)
    }

    private func loadVoiceSettings() async {
        do {
            let (data, response) = try await hostSession.data(from: serverURL.appendingPathComponent("voices"))
            guard let httpResponse = response as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode) else {
                let errorMessage = StatusBarViewController.extractHostErrorMessage(from: data) ?? "Stimmen konnten nicht geladen werden"
                await MainActor.run { [weak self] in
                    self?.setStatus(errorMessage)
                    self?.voicePopup.isEnabled = true
                }
                return
            }

            let settings = try JSONDecoder().decode(VoiceSettingsResponse.self, from: data)
            await MainActor.run { [weak self] in
                self?.applyVoiceSettings(settings, announceChange: false)
            }
        } catch {
            diagnosticsLogger.log("Voice settings load failed: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.setStatus(self?.describeHostError(error, fallback: "Stimmen konnten nicht geladen werden") ?? "Stimmen konnten nicht geladen werden")
                self?.voicePopup.isEnabled = true
            }
        }
    }

    private func submitVoiceSelection(_ voice: String) async {
        do {
            var request = URLRequest(url: serverURL.appendingPathComponent("voice"))
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(VoiceSelectionRequest(voice: voice))

            let (data, response) = try await hostSession.data(for: request)
            guard let httpResponse = response as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode) else {
                let errorMessage = StatusBarViewController.extractHostErrorMessage(from: data) ?? "Stimme konnte nicht gesetzt werden"
                await MainActor.run { [weak self] in
                    self?.setStatus(errorMessage)
                    self?.voicePopup.isEnabled = !(self?.isBusy ?? false)
                }
                return
            }

            let settings = try JSONDecoder().decode(VoiceSettingsResponse.self, from: data)
            await MainActor.run { [weak self] in
                self?.applyVoiceSettings(settings, announceChange: true)
            }
        } catch {
            diagnosticsLogger.log("Voice selection failed: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.setStatus(self?.describeHostError(error, fallback: "Stimme konnte nicht gesetzt werden") ?? "Stimme konnte nicht gesetzt werden")
                self?.voicePopup.isEnabled = !(self?.isBusy ?? false)
            }
        }
    }

    private func submitThinkingSelection(_ thinkingLevel: String) async {
        do {
            var request = URLRequest(url: serverURL.appendingPathComponent("thinking"))
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(ThinkingSelectionRequest(thinkingLevel: thinkingLevel))

            let (data, response) = try await hostSession.data(for: request)
            guard let httpResponse = response as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode) else {
                let errorMessage = StatusBarViewController.extractHostErrorMessage(from: data) ?? "Thinking-Level konnte nicht gesetzt werden"
                await MainActor.run { [weak self] in
                    self?.setStatus(errorMessage)
                    self?.thinkingPopup.isEnabled = !(self?.isBusy ?? false)
                }
                return
            }

            let settings = try JSONDecoder().decode(VoiceSettingsResponse.self, from: data)
            await MainActor.run { [weak self] in
                self?.applyVoiceSettings(settings, announceChange: false, announceThinkingChange: true)
            }
        } catch {
            diagnosticsLogger.log("Thinking selection failed: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.setStatus(self?.describeHostError(error, fallback: "Thinking-Level konnte nicht gesetzt werden") ?? "Thinking-Level konnte nicht gesetzt werden")
                self?.thinkingPopup.isEnabled = !(self?.isBusy ?? false)
            }
        }
    }

    private func startActivityPolling() {
        activityPollTimer?.invalidate()
        refreshActivityStatus()
        activityPollTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.refreshActivityStatus()
        }
    }

    private func refreshActivityStatus() {
        let url = URL(fileURLWithPath: Self.activityFilePath)
        guard let data = try? Data(contentsOf: url),
              let snapshot = try? JSONDecoder().decode(ActivitySnapshot.self, from: data) else {
            toolStatusLabel.stringValue = "Tool: -"
            return
        }

        if let activeTool = snapshot.ActiveTool?.trimmingCharacters(in: .whitespacesAndNewlines), !activeTool.isEmpty {
            toolStatusLabel.stringValue = "Tool: \(activeTool)"
        } else {
            toolStatusLabel.stringValue = "Tool: -"
        }
    }

    private func applyVoiceSettings(_ settings: VoiceSettingsResponse, announceChange: Bool, announceThinkingChange: Bool = false) {
        availableVoices = settings.availableVoices
        isUpdatingVoiceSelection = true
        voicePopup.removeAllItems()
        voicePopup.addItems(withTitles: settings.availableVoices.map(Self.displayName(forVoiceId:)))

        if let index = settings.availableVoices.firstIndex(of: settings.currentVoice) {
            voicePopup.selectItem(at: index)
        }

        isUpdatingVoiceSelection = false
        voicePopup.isEnabled = !isBusy

        availableThinkingLevels = settings.availableThinkingLevels
        isUpdatingThinkingSelection = true
        thinkingPopup.removeAllItems()
        thinkingPopup.addItems(withTitles: settings.availableThinkingLevels.map(Self.displayName(forThinkingLevel:)))

        if let index = settings.availableThinkingLevels.firstIndex(of: settings.currentThinkingLevel) {
            thinkingPopup.selectItem(at: index)
        }

        isUpdatingThinkingSelection = false
        thinkingPopup.isEnabled = !isBusy && !availableThinkingLevels.isEmpty

        if announceChange {
            setStatus("Stimme aktiv: \(Self.displayName(forVoiceId: settings.currentVoice))")
        } else if announceThinkingChange {
            setStatus("Thinking-Level aktiv: \(Self.displayName(forThinkingLevel: settings.currentThinkingLevel))")
        }
    }

    private static func displayName(forVoiceId voiceId: String) -> String {
        guard let firstCharacter = voiceId.first else {
            return voiceId
        }

        return String(firstCharacter).uppercased() + voiceId.dropFirst()
    }

    private static func displayName(forThinkingLevel thinkingLevel: String) -> String {
        switch thinkingLevel.lowercased() {
        case "low":
            return "Thinking: Low"
        case "medium":
            return "Thinking: Medium"
        case "high":
            return "Thinking: High"
        default:
            return "Thinking: Auto"
        }
    }

    private func presentUsage(_ usage: TokenUsage?) {
        diagnosticsLogger.log("Usage update: \(formatUsage(usage))")
        usageLabel.stringValue = formatUsage(usage)
        updateOverlayLayout(animated: false)
    }

    private func clearUsage() {
        diagnosticsLogger.log("Usage cleared")
        usageLabel.stringValue = formatUsage(nil)
        updateOverlayLayout(animated: false)
    }

    private func formatUsage(_ usage: TokenUsage?) -> String {
        guard let usage else {
            return "Input: - | Output: - | Total: -"
        }

        let derivedInput = usage.inputTokens
            ?? Self.sumUsageParts(usage.inputTextTokens, usage.inputAudioTokens, usage.inputImageTokens)
        let derivedOutput = usage.outputTokens
            ?? Self.sumUsageParts(usage.outputTextTokens, usage.outputAudioTokens)
        let derivedTotal = usage.totalTokens
            ?? Self.sumUsageParts(derivedInput, derivedOutput)

        let inputSummary = "Input: \(derivedInput.map(String.init) ?? "-")"
        let outputSummary = "Output: \(derivedOutput.map(String.init) ?? "-")"
        let totalSummary = "Total: \(derivedTotal.map(String.init) ?? "-")"
        let cacheSummary = usage.cachedInputTokens.map { " | Cache: \($0)" } ?? ""
        return "\(inputSummary) | \(outputSummary) | \(totalSummary)\(cacheSummary)"
    }

    private func describeHostError(_ error: Error, fallback: String) -> String {
        if let urlError = error as? URLError {
            switch urlError.code {
            case .timedOut:
                return "Die lokale AIDesk-Anfrage hat das Zeitlimit überschritten. Bitte erneut versuchen."
            case .cannotConnectToHost, .cannotFindHost:
                return "Der lokale AIDesk-Host ist momentan nicht erreichbar."
            case .networkConnectionLost:
                return "Die Verbindung zum lokalen AIDesk-Host wurde unterbrochen."
            case .cancelled:
                return "Die laufende Anfrage wurde abgebrochen."
            default:
                break
            }
        }

        return "\(fallback): \(error.localizedDescription)"
    }

    private static func sumUsageParts(_ values: Int?...) -> Int? {
        let resolvedValues = values.compactMap { $0 }
        guard !resolvedValues.isEmpty else {
            return nil
        }

        return resolvedValues.reduce(0, +)
    }

    private func configureRecordButton(isRecording: Bool, isAutoFollowUp: Bool = false) {
        recordButton.image = NSImage(
            systemSymbolName: isRecording ? "stop.circle.fill" : "mic.fill",
            accessibilityDescription: isRecording ? "Aufnahme stoppen" : "Aufnehmen")
        recordButton.contentTintColor = isRecording
            ? (isAutoFollowUp ? NSColor.systemOrange.withAlphaComponent(0.95) : NSColor.systemRed.withAlphaComponent(0.95))
            : Self.primaryTextColor
        recordButton.toolTip = isRecording
            ? (isAutoFollowUp ? "Automatische Folgeaufnahme aktiv" : "Aufnahme läuft")
            : "Aufnahme starten oder stoppen"
    }

    private func setBusy(_ busy: Bool) {
        isBusy = busy
        cancelButton.isEnabled = busy
        voicePopup.isEnabled = !busy && !availableVoices.isEmpty
        thinkingPopup.isEnabled = !busy && !availableThinkingLevels.isEmpty
        if busy {
            activityIndicator.startAnimation(nil)
        } else {
            activityIndicator.stopAnimation(nil)
        }
        setActivity(busy)
    }

    private func hasActiveWork() -> Bool {
        responseTask != nil || liveAudioSessionId != nil || audioPlayerNode.isPlaying || (audioPlayer?.isPlaying ?? false)
    }

    private static func extractHostErrorMessage(from data: Data) -> String? {
        guard data.isEmpty == false,
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let error = json["error"] as? String else {
            return nil
        }

        let trimmedError = error.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmedError.isEmpty ? nil : trimmedError
    }

    private static func readPositiveDouble(_ value: String?, fallback: Double) -> Double {
        guard let value, let parsed = Double(value), parsed > 0 else {
            return fallback
        }

        return parsed
    }

    private func dismissForAutomation() {
        dismissPopover()
        NSApp.hide(nil)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let serverURL: URL
    private let diagnosticsLogger = StatusBarDiagnosticsLogger()
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private var inputWindow: NSPanel?
    private var logWindow: NSWindow?
    private weak var viewController: StatusBarViewController?
    private var isBusy = false

    init(serverURL: URL) {
        self.serverURL = serverURL
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let viewController = StatusBarViewController(serverURL: serverURL, dismissPopover: { [weak self] in
            self?.inputWindow?.orderOut(nil)
        }, setActivity: { [weak self] isBusy in
            self?.setBusy(isBusy)
        }, toggleLogWindow: { [weak self] in
            self?.toggleLogWindow(nil)
        }, diagnosticsLogger: diagnosticsLogger)
        self.viewController = viewController
        inputWindow = makeInputWindow(contentViewController: viewController)
        logWindow = makeLogWindow()
        diagnosticsLogger.log("Application launched with server URL \(serverURL.absoluteString)")

        if let button = statusItem.button {
            button.target = self
            button.action = #selector(togglePopover(_:))
        }
        updateStatusItemTitle()

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.75) {
            viewController.runSelfTestIfConfigured()
        }
    }

    @objc private func togglePopover(_ sender: AnyObject?) {
        guard let inputWindow else {
            return
        }

        if inputWindow.isVisible {
            inputWindow.orderOut(sender)
        } else {
            positionInputWindow(inputWindow)
            NSApp.activate(ignoringOtherApps: true)
            inputWindow.makeKeyAndOrderFront(sender)
            DispatchQueue.main.async { [weak self] in
                self?.viewController?.focusTextField()
            }
        }
    }

    @objc private func toggleLogWindow(_ sender: AnyObject?) {
        guard let logWindow else {
            return
        }

        if logWindow.isVisible {
            logWindow.orderOut(sender)
        } else {
            positionLogWindow(logWindow)
            NSApp.activate(ignoringOtherApps: true)
            logWindow.makeKeyAndOrderFront(sender)
        }
    }

    private func makeInputWindow(contentViewController: NSViewController) -> NSPanel {
        let panel = OverlayPanel(
            contentRect: NSRect(x: 0, y: 0, width: 460, height: 216),
            styleMask: [.borderless, .nonactivatingPanel, .fullSizeContentView],
            backing: .buffered,
            defer: false)
        panel.contentViewController = contentViewController
        panel.title = "AIDeskAssistantOverlayPanel"
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = false
        panel.isMovableByWindowBackground = true
        panel.hasShadow = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        positionInputWindow(panel)
        return panel
    }

    private func makeLogWindow() -> NSWindow {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 760, height: 460),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.contentViewController = ActivityLogViewController()
        window.title = "AIDeskAssistantLogWindow"
        window.titleVisibility = .visible
        window.isReleasedWhenClosed = false
        window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.minSize = NSSize(width: 520, height: 300)
        positionLogWindow(window)
        return window
    }

    private func positionInputWindow(_ window: NSWindow) {
        let targetScreen = window.screen ?? NSScreen.main ?? NSScreen.screens.first
        let visibleFrame = targetScreen?.visibleFrame ?? NSScreen.screens.first?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let frame = window.frame
        let x = visibleFrame.midX - (frame.width / 2)
        let y = visibleFrame.minY + 24
        window.setFrameOrigin(NSPoint(x: round(x), y: round(y)))
    }

    private func positionLogWindow(_ window: NSWindow) {
        let targetScreen = window.screen ?? NSScreen.main ?? NSScreen.screens.first
        let visibleFrame = targetScreen?.visibleFrame ?? NSScreen.screens.first?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let frame = window.frame
        let x = visibleFrame.maxX - frame.width - 24
        let y = visibleFrame.minY + 24
        window.setFrameOrigin(NSPoint(x: round(x), y: round(y)))
    }

    private func setBusy(_ busy: Bool) {
        isBusy = busy
        updateStatusItemTitle()
    }

    private func updateStatusItemTitle() {
        guard let button = statusItem.button else {
            return
        }

        button.title = isBusy ? "AIDesk •" : "AIDesk"
    }
}

guard CommandLine.arguments.count >= 2, let serverURL = URL(string: CommandLine.arguments[1]) else {
    fputs("Usage: AIDeskAssistantStatusBar.swift <server-url>\n", stderr)
    exit(2)
}

let application = NSApplication.shared
let delegate = AppDelegate(serverURL: serverURL)
application.setActivationPolicy(.accessory)
application.delegate = delegate
application.run()