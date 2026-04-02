import Cocoa
import AVFoundation

struct AssistantResponse: Decodable {
    let text: String?
    let audioBase64: String?
    let audioMimeType: String?
    let error: String?
}

struct AssistantStreamEvent: Decodable {
    let type: String
    let text: String?
    let pcmBase64: String?
    let sampleRate: Int?
    let audioFormat: String?
    let error: String?
}

struct AudioLiveStartResponse: Decodable {
    let sessionId: String
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

final class StatusBarViewController: NSViewController, NSTextFieldDelegate {
    private let serverURL: URL
    private let dismissPopover: () -> Void
    private let diagnosticsLogger: StatusBarDiagnosticsLogger
    private let textField = NSTextField(frame: .zero)
    private let statusLabel = NSTextField(labelWithString: "Bereit")
    private let sendButton = NSButton(title: "Senden", target: nil, action: nil)
    private let recordButton = NSButton(title: "Aufnehmen", target: nil, action: nil)
    private let quitButton = NSButton(title: "Beenden", target: nil, action: nil)

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
    private var playbackEngineConfigured = false
    private var accumulatedResponseText = ""

    init(serverURL: URL, dismissPopover: @escaping () -> Void, diagnosticsLogger: StatusBarDiagnosticsLogger) {
        self.serverURL = serverURL
        self.dismissPopover = dismissPopover
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
        view = NSView(frame: NSRect(x: 0, y: 0, width: 360, height: 210))

        textField.placeholderString = "Text eingeben"
        textField.delegate = self
        textField.frame = NSRect(x: 16, y: 150, width: 328, height: 28)

        statusLabel.frame = NSRect(x: 16, y: 110, width: 328, height: 32)
        statusLabel.lineBreakMode = .byWordWrapping

        sendButton.frame = NSRect(x: 16, y: 62, width: 96, height: 32)
        sendButton.target = self
        sendButton.action = #selector(sendText)

        recordButton.frame = NSRect(x: 128, y: 62, width: 112, height: 32)
        recordButton.target = self
        recordButton.action = #selector(toggleRecording)

        quitButton.frame = NSRect(x: 256, y: 62, width: 88, height: 32)
        quitButton.target = self
        quitButton.action = #selector(quitApp)

        view.addSubview(textField)
        view.addSubview(statusLabel)
        view.addSubview(sendButton)
        view.addSubview(recordButton)
        view.addSubview(quitButton)
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        view.window?.makeFirstResponder(textField)
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

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(insertNewline(_:)) {
            sendText(nil)
            return true
        }

        return false
    }

    @objc private func sendText(_ sender: Any?) {
        let text = textField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            setStatus("Bitte Text eingeben")
            return
        }

        textField.stringValue = ""
        submitText(text)
    }

    @objc private func toggleRecording(_ sender: Any?) {
        if liveAudioSessionId != nil {
            stopRecordingAndSend(autoTriggered: false)
        } else {
            startRecording()
        }
    }

    @objc private func quitApp(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    private func startRecording() {
        recordButton.isEnabled = false
        setStatus("Starte Live-Mikrofon …")

        Task { [weak self] in
            await self?.interruptCurrentResponse(sendRemoteCancel: true)
            await self?.beginLiveRecording()
        }
    }

    private func stopRecordingAndSend(autoTriggered: Bool) {
        stopCaptureEngine()
        recordButton.title = "Aufnehmen"
        recordButton.isEnabled = true

        guard let sessionId = liveAudioSessionId else {
            diagnosticsLogger.log("No live audio session to commit")
            setStatus("Keine laufende Mikrofonaufnahme")
            return
        }

        liveAudioSessionId = nil

        setStatus(autoTriggered ? "Stille erkannt, verarbeite Live-Audio …" : "Verarbeite Live-Audio …")
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
            startAutoCommitMonitor(sessionId: sessionId)

            await MainActor.run { [weak self] in
                self?.recordButton.title = "Stop"
                self?.recordButton.isEnabled = true
                self?.setStatus("Nehme live auf … Stop ist optional.")
            }

            diagnosticsLogger.log("Live audio thresholds: peak=\(String(format: "%.4f", silenceAmplitudeThreshold)) rmsMin=\(String(format: "%.4f", minimumRmsThreshold)) silence=\(String(format: "%.2f", silenceCommitInterval))s calibration=\(String(format: "%.2f", rmsCalibrationDuration))s")
            diagnosticsLogger.log("Live recording started with session \(sessionId)")
        } catch {
            diagnosticsLogger.log("Failed to start live recording: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.recordButton.title = "Aufnehmen"
                self?.recordButton.isEnabled = true
                self?.setStatus("Live-Aufnahmefehler: \(error.localizedDescription)")
            }
        }
    }

    private func startStreamingRequest(_ request: URLRequest, sendRemoteCancel: Bool = true) {
        let previousResponseTask = responseTask
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
        dismissForAutomation()
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
            liveAudioSessionId = nil
            recordButton.title = "Aufnehmen"
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
            }
            semaphore.signal()
        }.resume()
        semaphore.wait()
    }

    private func consumeStreamingResponse(_ request: URLRequest) async {
        do {
            let (bytes, response) = try await URLSession.shared.bytes(for: request)
            if let httpResponse = response as? HTTPURLResponse, !(200...299).contains(httpResponse.statusCode) {
                await MainActor.run { [weak self] in
                    self?.setStatus("Serverfehler: \(httpResponse.statusCode)")
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
            return
        } catch {
            diagnosticsLogger.log("Streaming request error: \(error.localizedDescription)")
            await MainActor.run { [weak self] in
                self?.setStatus("Fehler: \(error.localizedDescription)")
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
            return
        }

        let spokenOrText = response.text?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let spokenOrText, !spokenOrText.isEmpty {
            setStatus(spokenOrText)
        } else {
            setStatus("Antwort empfangen")
        }

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

        case "error":
            diagnosticsLogger.log("Received error event: \(event.error ?? "<none>")")
            if let error = event.error, !error.isEmpty {
                setStatus("Fehler: \(error)")
            } else {
                setStatus("Realtime-Stream fehlgeschlagen")
            }

        case "cancelled":
            diagnosticsLogger.log("Received cancelled event")
            if accumulatedResponseText.isEmpty {
                setStatus("Antwort abgebrochen")
            }

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
    private let popover = NSPopover()

    init(serverURL: URL) {
        self.serverURL = serverURL
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let viewController = StatusBarViewController(serverURL: serverURL, dismissPopover: { [weak self] in
            self?.popover.performClose(nil)
        }, diagnosticsLogger: diagnosticsLogger)
        popover.contentViewController = viewController
        popover.behavior = .transient
        diagnosticsLogger.log("Application launched with server URL \(serverURL.absoluteString)")

        if let button = statusItem.button {
            button.title = "AIDesk"
            button.target = self
            button.action = #selector(togglePopover(_:))
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.75) {
            viewController.runSelfTestIfConfigured()
        }
    }

    @objc private func togglePopover(_ sender: AnyObject?) {
        guard let button = statusItem.button else {
            return
        }

        if popover.isShown {
            popover.performClose(sender)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            NSApp.activate(ignoringOtherApps: true)
        }
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