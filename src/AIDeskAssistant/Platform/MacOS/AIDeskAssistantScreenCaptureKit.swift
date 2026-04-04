import Cocoa
import CoreGraphics
import ScreenCaptureKit

private struct Configuration {
    let outputPath: String
    let pointX: Int?
    let pointY: Int?
    let excludedTitles: [String]
}

private struct CaptureMetadata: Encodable {
    let displayX: Int
    let displayY: Int
    let displayWidth: Int
    let displayHeight: Int
}

private enum CaptureError: Error, LocalizedError {
    case missingOutputPath
    case invalidArgument(String)
    case noDisplayFound
    case captureFailed
    case pngEncodingFailed

    var errorDescription: String? {
        switch self {
        case .missingOutputPath:
            return "Missing required --output path."
        case .invalidArgument(let argument):
            return "Invalid argument: \(argument)"
        case .noDisplayFound:
            return "No matching display found for ScreenCaptureKit capture."
        case .captureFailed:
            return "ScreenCaptureKit capture failed."
        case .pngEncodingFailed:
            return "Failed to encode capture as PNG."
        }
    }
}

@main
struct ScreenCaptureKitMain {
    static func main() async {
        do {
            let configuration = try parseArguments(CommandLine.arguments)
            let metadata = try await captureScreenshot(configuration: configuration)
            let jsonData = try JSONEncoder().encode(metadata)
            if let json = String(data: jsonData, encoding: .utf8) {
                print(json)
            }
        } catch {
            fputs("\(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }

    private static func parseArguments(_ arguments: [String]) throws -> Configuration {
        var outputPath: String?
        var pointX: Int?
        var pointY: Int?
        var excludedTitles: [String] = []

        var index = 1
        while index < arguments.count {
            let argument = arguments[index]
            switch argument {
            case "--output":
                index += 1
                guard index < arguments.count else { throw CaptureError.missingOutputPath }
                outputPath = arguments[index]
            case "--point-x":
                index += 1
                guard index < arguments.count, let value = Int(arguments[index]) else { throw CaptureError.invalidArgument(argument) }
                pointX = value
            case "--point-y":
                index += 1
                guard index < arguments.count, let value = Int(arguments[index]) else { throw CaptureError.invalidArgument(argument) }
                pointY = value
            case "--exclude-title":
                index += 1
                guard index < arguments.count else { throw CaptureError.invalidArgument(argument) }
                excludedTitles.append(arguments[index])
            default:
                throw CaptureError.invalidArgument(argument)
            }

            index += 1
        }

        guard let outputPath else {
            throw CaptureError.missingOutputPath
        }

        return Configuration(outputPath: outputPath, pointX: pointX, pointY: pointY, excludedTitles: excludedTitles)
    }

    private static func captureScreenshot(configuration: Configuration) async throws -> CaptureMetadata {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = selectDisplay(from: content.displays, pointX: configuration.pointX, pointY: configuration.pointY) else {
            throw CaptureError.noDisplayFound
        }

        let excludedWindows = content.windows.filter { window in
            guard let title = window.title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty else {
                return false
            }

            return configuration.excludedTitles.contains { excluded in
                title.caseInsensitiveCompare(excluded) == .orderedSame
            }
        }

        let filter = SCContentFilter(display: display, excludingWindows: excludedWindows)
        let streamConfiguration = SCStreamConfiguration()
        streamConfiguration.width = Int(display.frame.width)
        streamConfiguration.height = Int(display.frame.height)
        streamConfiguration.showsCursor = true

        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: streamConfiguration)
        let bitmap = NSBitmapImageRep(cgImage: image)
        guard let pngData = bitmap.representation(using: .png, properties: [:]) else {
            throw CaptureError.pngEncodingFailed
        }

        try pngData.write(to: URL(fileURLWithPath: configuration.outputPath), options: .atomic)
        return CaptureMetadata(
            displayX: Int(display.frame.origin.x.rounded()),
            displayY: Int(display.frame.origin.y.rounded()),
            displayWidth: Int(display.frame.width.rounded()),
            displayHeight: Int(display.frame.height.rounded()))
    }

    private static func selectDisplay(from displays: [SCDisplay], pointX: Int?, pointY: Int?) -> SCDisplay? {
        if let pointX, let pointY {
            let point = CGPoint(x: pointX, y: pointY)
            if let matchingDisplay = displays.first(where: { $0.frame.contains(point) }) {
                return matchingDisplay
            }
        }

        let mainDisplayID = CGMainDisplayID()
        if let mainDisplay = displays.first(where: { $0.displayID == mainDisplayID }) {
            return mainDisplay
        }

        return displays.first
    }
}