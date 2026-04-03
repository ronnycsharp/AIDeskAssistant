import Cocoa
import AVFoundation

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
}

struct VoiceSelectionRequest: Encodable {
    let voice: String
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

final class StatusBarViewController: NSViewController, NSTextViewDelegate {
    private static let panelWidth: CGFloat = 460
    private static let minimumPanelHeight: CGFloat = 206
    private static let maximumPanelHeight: CGFloat = 278
    private static let backgroundInset: CGFloat = 10
    private static let sideInset: CGFloat = 28
    private static let textHorizontalInset: CGFloat = 20
    private static let topInset: CGFloat = 34
    private static let bottomInset: CGFloat = 12
    private static let defaultTextHeight: CGFloat = 36
    private static let maximumTextHeight: CGFloat = 108

    private let serverURL: URL
    private let dismissPopover: () -> Void
    private let setActivity: (Bool) -> Void
    private let diagnosticsLogger: StatusBarDiagnosticsLogger
    private let backgroundView = NSVisualEffectView(frame: .zero)
    private let voicePopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let textScrollView = NSScrollView(frame: .zero)
    private let textView = NSTextView(frame: .zero)
    private let statusLabel = NSTextField(labelWithString: "")
    private let usageLabel = NSTextField(labelWithString: "Input: - | Output: - | Total: -")
    private let activityIndicator = NSProgressIndicator(frame: .zero)
    private let recordButton = NSButton(frame: .zero)
    private let cancelButton = NSButton(title: "Cancel", target: nil, action: nil)
    private let quitButton = NSButton(title: "AIDesk beenden", target: nil, action: nil)
    private var currentPanelHeight: CGFloat = minimumPanelHeight

    private var audioPlayer: AVAudioPlayer?
    private var responseTask: Task<Void, Never>?
    private let audioEngine = AVAudioEngine()
    private let audioPlayerNode = AVAudioPlayerNode()
    private let playbackFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000, channels: 1, interleaved: false)!
    private let captureEngine = AVAudioEngine()
    private let captureFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 24_000, channels: 1, interleaved: true)!
    private let captureQueue = DispatchQueue(label: "aidesk.statusbar.capture")
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
    private var isUpdatingVoiceSelection = false
    private var isBusy = false

    init(serverURL: URL, dismissPopover: @escaping () -> Void, setActivity: @escaping (Bool) -> Void, diagnosticsLogger: StatusBarDiagnosticsLogger) {
        self.serverURL = serverURL
        self.dismissPopover = dismissPopover
        self.setActivity = setActivity
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

    override func loadView() {
        view = NSView(frame: NSRect(x: 0, y: 0, width: Self.panelWidth, height: Self.minimumPanelHeight))
        view.wantsLayer = true
        view.layer?.backgroundColor = NSColor.clear.cgColor

        backgroundView.frame = view.bounds.insetBy(dx: Self.backgroundInset, dy: Self.backgroundInset)
        backgroundView.autoresizingMask = [.width, .height]
        backgroundView.material = .hudWindow
        backgroundView.blendingMode = .withinWindow
        backgroundView.state = .active
        backgroundView.wantsLayer = true
        backgroundView.layer?.cornerRadius = 24
        backgroundView.layer?.masksToBounds = true
        backgroundView.layer?.borderWidth = 1
        backgroundView.layer?.borderColor = NSColor.white.withAlphaComponent(0.14).cgColor
        view.addSubview(backgroundView)

        voicePopup.target = self
        voicePopup.action = #selector(changeVoice(_:))
        voicePopup.bezelStyle = .rounded
        voicePopup.contentTintColor = NSColor.white.withAlphaComponent(0.92)

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
        textView.textColor = .white
        textView.backgroundColor = .clear
        textView.insertionPointColor = .white
        textView.delegate = self
        textScrollView.documentView = textView

        statusLabel.lineBreakMode = .byWordWrapping
        statusLabel.maximumNumberOfLines = 1
        statusLabel.font = NSFont.systemFont(ofSize: 14, weight: .medium)
        statusLabel.textColor = NSColor.white.withAlphaComponent(0.92)
        statusLabel.isHidden = true

        usageLabel.lineBreakMode = .byWordWrapping
        usageLabel.maximumNumberOfLines = 1
        usageLabel.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        usageLabel.textColor = NSColor.white.withAlphaComponent(0.58)

        activityIndicator.style = .spinning
        activityIndicator.controlSize = .small
        activityIndicator.isDisplayedWhenStopped = false

        recordButton.target = self
        recordButton.action = #selector(toggleRecording)
        recordButton.bezelStyle = .rounded
        recordButton.image = NSImage(systemSymbolName: "mic.fill", accessibilityDescription: "Aufnehmen")
        recordButton.imagePosition = .imageOnly
        recordButton.contentTintColor = NSColor.white.withAlphaComponent(0.92)
        recordButton.toolTip = "Aufnahme starten oder stoppen"

        cancelButton.target = self
        cancelButton.action = #selector(cancelCurrentWork)
        cancelButton.isEnabled = false
        cancelButton.bezelStyle = .rounded
        cancelButton.image = NSImage(systemSymbolName: "stop.fill", accessibilityDescription: "Abbrechen")
        cancelButton.imagePosition = .imageOnly
        cancelButton.contentTintColor = NSColor.white.withAlphaComponent(0.92)
        cancelButton.toolTip = "Laufende Antwort abbrechen"

        quitButton.target = self
        quitButton.action = #selector(quitApp)
        quitButton.bezelStyle = .rounded

        backgroundView.addSubview(voicePopup)
        backgroundView.addSubview(textScrollView)
        backgroundView.addSubview(statusLabel)
        backgroundView.addSubview(usageLabel)
        backgroundView.addSubview(activityIndicator)
        backgroundView.addSubview(recordButton)
        backgroundView.addSubview(cancelButton)
        backgroundView.addSubview(quitButton)

        configureRecordButton(isRecording: false)
        updateOverlayLayout(animated: false)
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        focusTextField()
        Task { [weak self] in
            await self?.loadVoiceSettings()
        }
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

            sendText(nil)
            return true
        }

        if commandSelector == #selector(cancelOperation(_:)) {
            dismissPopover()
            return true
        }

        return false
    }

    @objc private func sendText(_ sender: Any?) {
        let text = textView.string.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            setStatus("")
            return
        }

        textView.string = ""
        updateOverlayLayout(animated: true)
        submitText(text)
        focusTextField()
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

    @objc private func toggleRecording(_ sender: Any?) {
        if liveAudioSessionId != nil {
            stopRecordingAndSend(autoTriggered: false)
        } else {
            startRecording()
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

    private func startRecording() {
        recordButton.isEnabled = false
        setStatus("Starte Live-Mikrofon …")
        clearUsage()
        setBusy(true)

        Task { [weak self] in
            await self?.interruptCurrentResponse(sendRemoteCancel: true)
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

        liveAudioSessionId = nil

        setStatus(autoTriggered ? "Stille erkannt, verarbeite Live-Audio …" : "Verarbeite Live-Audio …")
        setBusy(true)
        dismissForAutomation()
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

        startStreamingRequest(request, sendRemoteCancel: false)
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
                self?.configureRecordButton(isRecording: true)
                self?.recordButton.isEnabled = true
                self?.setStatus("Nehme live auf … Stop ist optional.")
            }

            diagnosticsLogger.log("Live audio thresholds: peak=\(String(format: "%.4f", silenceAmplitudeThreshold)) rmsMin=\(String(format: "%.4f", minimumRmsThreshold)) silence=\(String(format: "%.2f", silenceCommitInterval))s calibration=\(String(format: "%.2f", rmsCalibrationDuration))s")
            diagnosticsLogger.log("Live recording started with session \(sessionId)")
        } catch {
            diagnosticsLogger.log("Failed to start live recording: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.configureRecordButton(isRecording: false)
                self?.recordButton.isEnabled = true
                self?.setStatus("Live-Aufnahmefehler: \(error.localizedDescription)")
            }
        }
    }

    private func startStreamingRequest(_ request: URLRequest, sendRemoteCancel: Bool = true) {
        let previousResponseTask = responseTask
        clearUsage()
        setBusy(true)
        responseTask = Task { [weak self] in
            guard let self else {
                return
            }

            await self.interruptCurrentResponse(previousResponseTask: previousResponseTask, sendRemoteCancel: sendRemoteCancel)
            self.diagnosticsLogger.log("Starting streaming request to \(request.url?.absoluteString ?? "<unknown>")")
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
            let (_, response) = try await URLSession.shared.data(for: request)
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
            let (_, response) = try await URLSession.shared.data(for: request)
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

        let (data, response) = try await URLSession.shared.data(for: request)
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
                      self.hasDetectedSpeech,
                      let lastSpeechAt = self.lastSpeechAt,
                      let recordingStartedAt = self.recordingStartedAt else {
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
        URLSession.shared.dataTask(with: request) { [weak self] _, response, error in
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
            let (bytes, response) = try await URLSession.shared.bytes(for: request)
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
        } catch is CancellationError {
            diagnosticsLogger.log("Streaming request cancelled")
            await MainActor.run { [weak self] in
                self?.setBusy(false)
            }
            return
        } catch {
            diagnosticsLogger.log("Streaming request error: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.setStatus("Fehler: \(error.localizedDescription)")
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

        case "error":
            diagnosticsLogger.log("Received error event: \(event.error ?? "<none>")")
            if let error = event.error, !error.isEmpty {
                setStatus("Fehler: \(error)")
            } else {
                setStatus("Realtime-Stream fehlgeschlagen")
            }
            setBusy(false)

        case "cancelled":
            diagnosticsLogger.log("Received cancelled event")
            if accumulatedResponseText.isEmpty {
                setStatus("Antwort abgebrochen")
            }
            clearUsage()
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

        audioPlayerNode.scheduleBuffer(buffer, completionHandler: nil)
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
        if audioEngine.isRunning {
            audioEngine.stop()
        }
        diagnosticsLogger.log("Playback stopped")
    }

    private func setStatus(_ text: String) {
        diagnosticsLogger.log("Status: \(text)")
        statusLabel.stringValue = text
        statusLabel.isHidden = text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        updateOverlayLayout(animated: true)
    }

    private func updateOverlayLayout(animated: Bool) {
        let contentTextHeight = calculatedContentTextHeight()
        let textHeight = min(Self.maximumTextHeight, max(Self.defaultTextHeight, contentTextHeight))
        let desiredPanelHeight = min(Self.maximumPanelHeight, max(Self.minimumPanelHeight, textHeight + 170))
        currentPanelHeight = desiredPanelHeight

        view.frame = NSRect(x: 0, y: 0, width: Self.panelWidth, height: desiredPanelHeight)
        backgroundView.frame = view.bounds.insetBy(dx: Self.backgroundInset, dy: Self.backgroundInset)

        let contentHeight = backgroundView.bounds.height
        let contentWidth = backgroundView.bounds.width
        let topRowY = contentHeight - Self.topInset - 30
        let textY = topRowY - 12 - textHeight
        let statusY = textY - 30
        let bottomRowY = Self.bottomInset

        voicePopup.frame = NSRect(x: Self.sideInset, y: topRowY, width: 170, height: 30)
        recordButton.frame = NSRect(x: contentWidth - Self.sideInset - 74, y: topRowY, width: 34, height: 30)
        cancelButton.frame = NSRect(x: contentWidth - Self.sideInset - 34, y: topRowY, width: 34, height: 30)

        textScrollView.frame = NSRect(x: Self.textHorizontalInset, y: textY, width: contentWidth - (Self.textHorizontalInset * 2), height: textHeight)
        textView.textContainer?.containerSize = NSSize(width: textScrollView.contentSize.width, height: CGFloat.greatestFiniteMagnitude)
        textView.frame = NSRect(origin: .zero, size: NSSize(width: textScrollView.contentSize.width, height: max(textHeight, contentTextHeight)))

        statusLabel.frame = NSRect(x: Self.sideInset, y: statusY, width: 320, height: 22)
        activityIndicator.frame = NSRect(x: contentWidth - Self.sideInset - 18, y: statusY + 2, width: 18, height: 18)
        usageLabel.frame = NSRect(x: Self.sideInset, y: bottomRowY + 8, width: 240, height: 18)
        quitButton.frame = NSRect(x: contentWidth - Self.sideInset - 150, y: bottomRowY, width: 150, height: 34)

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
            let (data, response) = try await URLSession.shared.data(from: serverURL.appendingPathComponent("voices"))
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
                self?.setStatus("Stimmen konnten nicht geladen werden")
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

            let (data, response) = try await URLSession.shared.data(for: request)
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
                self?.setStatus("Stimme konnte nicht gesetzt werden")
                self?.voicePopup.isEnabled = !(self?.isBusy ?? false)
            }
        }
    }

    private func applyVoiceSettings(_ settings: VoiceSettingsResponse, announceChange: Bool) {
        availableVoices = settings.availableVoices
        isUpdatingVoiceSelection = true
        voicePopup.removeAllItems()
        voicePopup.addItems(withTitles: settings.availableVoices.map(Self.displayName(forVoiceId:)))

        if let index = settings.availableVoices.firstIndex(of: settings.currentVoice) {
            voicePopup.selectItem(at: index)
        }

        isUpdatingVoiceSelection = false
        voicePopup.isEnabled = !isBusy

        if announceChange {
            setStatus("Stimme aktiv: \(Self.displayName(forVoiceId: settings.currentVoice))")
        }
    }

    private static func displayName(forVoiceId voiceId: String) -> String {
        guard let firstCharacter = voiceId.first else {
            return voiceId
        }

        return String(firstCharacter).uppercased() + voiceId.dropFirst()
    }

    private func presentUsage(_ usage: TokenUsage?) {
        usageLabel.stringValue = formatUsage(usage)
    }

    private func clearUsage() {
        usageLabel.stringValue = formatUsage(nil)
    }

    private func formatUsage(_ usage: TokenUsage?) -> String {
        guard let usage else {
            return "Input: - | Output: - | Total: -"
        }

        let inputSummary = "Input: \(usage.inputTokens.map(String.init) ?? "-")"
        let outputSummary = "Output: \(usage.outputTokens.map(String.init) ?? "-")"
        let totalSummary = "Total: \(usage.totalTokens.map(String.init) ?? "-")"
        return "\(inputSummary) | \(outputSummary) | \(totalSummary)"
    }

    private func configureRecordButton(isRecording: Bool) {
        recordButton.image = NSImage(
            systemSymbolName: isRecording ? "stop.circle.fill" : "mic.fill",
            accessibilityDescription: isRecording ? "Aufnahme stoppen" : "Aufnehmen")
        recordButton.contentTintColor = isRecording
            ? NSColor.systemRed.withAlphaComponent(0.95)
            : NSColor.white.withAlphaComponent(0.92)
    }

    private func setBusy(_ busy: Bool) {
        isBusy = busy
        cancelButton.isEnabled = busy
        voicePopup.isEnabled = !busy && !availableVoices.isEmpty
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
        }, diagnosticsLogger: diagnosticsLogger)
        self.viewController = viewController
        inputWindow = makeInputWindow(contentViewController: viewController)
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

    private func makeInputWindow(contentViewController: NSViewController) -> NSPanel {
        let panel = OverlayPanel(
            contentRect: NSRect(x: 0, y: 0, width: 460, height: 206),
            styleMask: [.borderless, .nonactivatingPanel, .fullSizeContentView],
            backing: .buffered,
            defer: false)
        panel.contentViewController = contentViewController
        panel.title = "AIDesk"
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

    private func positionInputWindow(_ window: NSWindow) {
        let targetScreen = window.screen ?? NSScreen.main ?? NSScreen.screens.first
        let visibleFrame = targetScreen?.visibleFrame ?? NSScreen.screens.first?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let frame = window.frame
        let x = visibleFrame.midX - (frame.width / 2)
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

        button.title = "AIDesk"
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