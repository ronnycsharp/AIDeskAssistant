import Cocoa
import AVFoundation

struct AssistantResponse: Decodable {
    let text: String?
    let audioBase64: String?
    let audioMimeType: String?
    let error: String?
}

final class StatusBarViewController: NSViewController, NSTextFieldDelegate {
    private let serverURL: URL
    private let textField = NSTextField(frame: .zero)
    private let statusLabel = NSTextField(labelWithString: "Bereit")
    private let sendButton = NSButton(title: "Senden", target: nil, action: nil)
    private let recordButton = NSButton(title: "Aufnehmen", target: nil, action: nil)
    private let quitButton = NSButton(title: "Beenden", target: nil, action: nil)

    private var audioRecorder: AVAudioRecorder?
    private var audioPlayer: AVAudioPlayer?
    private var recordedFileURL: URL?

    init(serverURL: URL) {
        self.serverURL = serverURL
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
        setStatus("Sende Nachricht …")

        var request = URLRequest(url: serverURL.appendingPathComponent("message"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["text": text])

        URLSession.shared.dataTask(with: request) { [weak self] data, _, error in
            self?.handleResponse(data: data, error: error)
        }.resume()
    }

    @objc private func toggleRecording(_ sender: Any?) {
        if audioRecorder?.isRecording == true {
            stopRecordingAndSend()
        } else {
            startRecording()
        }
    }

    @objc private func quitApp(_ sender: Any?) {
        NSApp.terminate(nil)
    }

    private func startRecording() {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
        let url = directory.appendingPathComponent("aidesk-recording.wav")
        recordedFileURL = url

        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 24_000,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false
        ]

        do {
            audioRecorder = try AVAudioRecorder(url: url, settings: settings)
            audioRecorder?.prepareToRecord()
            if audioRecorder?.record() == true {
                recordButton.title = "Stop"
                setStatus("Nehme auf …")
            } else {
                setStatus("Aufnahme konnte nicht gestartet werden")
            }
        } catch {
            setStatus("Aufnahmefehler: \(error.localizedDescription)")
        }
    }

    private func stopRecordingAndSend() {
        audioRecorder?.stop()
        audioRecorder = nil
        recordButton.title = "Aufnehmen"

        guard let fileURL = recordedFileURL else {
            setStatus("Keine Aufnahme gefunden")
            return
        }

        do {
            let data = try Data(contentsOf: fileURL)
            setStatus("Sende Audio …")

            var request = URLRequest(url: serverURL.appendingPathComponent("audio"))
            request.httpMethod = "POST"
            request.setValue("audio/wav", forHTTPHeaderField: "Content-Type")
            request.httpBody = data

            URLSession.shared.dataTask(with: request) { [weak self] data, _, error in
                self?.handleResponse(data: data, error: error)
            }.resume()
        } catch {
            setStatus("Konnte Aufnahme nicht lesen: \(error.localizedDescription)")
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

    private func setStatus(_ text: String) {
        statusLabel.stringValue = text
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let serverURL: URL
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let popover = NSPopover()

    init(serverURL: URL) {
        self.serverURL = serverURL
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let viewController = StatusBarViewController(serverURL: serverURL)
        popover.contentViewController = viewController
        popover.behavior = .transient

        if let button = statusItem.button {
            button.title = "AIDesk"
            button.target = self
            button.action = #selector(togglePopover(_:))
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