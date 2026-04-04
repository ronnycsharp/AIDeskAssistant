import AppKit
import Foundation
import Vision

private struct Configuration {
    let inputPath: String
}

private struct OcrPayload: Encodable {
    let fullText: String
    let lines: [OcrLine]

    enum CodingKeys: String, CodingKey {
        case fullText = "FullText"
        case lines = "Lines"
    }
}

private struct OcrLine: Encodable {
    let text: String
    let confidence: Double
    let x: Int
    let y: Int
    let width: Int
    let height: Int

    enum CodingKeys: String, CodingKey {
        case text = "Text"
        case confidence = "Confidence"
        case x = "X"
        case y = "Y"
        case width = "Width"
        case height = "Height"
    }
}

private enum OcrError: Error, LocalizedError {
    case missingInputPath
    case invalidArgument(String)
    case imageLoadFailed
    case cgImageUnavailable
    case requestFailed(String)

    var errorDescription: String? {
        switch self {
        case .missingInputPath:
            return "Missing required --input path."
        case .invalidArgument(let argument):
            return "Invalid argument: \(argument)"
        case .imageLoadFailed:
            return "Failed to load input image for OCR."
        case .cgImageUnavailable:
            return "Could not create CGImage for OCR request."
        case .requestFailed(let message):
            return message
        }
    }
}

private func parseArguments(_ arguments: [String]) throws -> Configuration {
    var inputPath: String?
    var index = 1

    while index < arguments.count {
        let argument = arguments[index]
        switch argument {
        case "--input":
            index += 1
            guard index < arguments.count else { throw OcrError.missingInputPath }
            inputPath = arguments[index]
        default:
            throw OcrError.invalidArgument(argument)
        }

        index += 1
    }

    guard let inputPath else {
        throw OcrError.missingInputPath
    }

    return Configuration(inputPath: inputPath)
}

private func recognizeText(configuration: Configuration) throws -> OcrPayload {
    let imageUrl = URL(fileURLWithPath: configuration.inputPath)
    guard let image = NSImage(contentsOf: imageUrl) else {
        throw OcrError.imageLoadFailed
    }

    var proposedRect = NSRect(origin: .zero, size: image.size)
    guard let cgImage = image.cgImage(forProposedRect: &proposedRect, context: nil, hints: nil) else {
        throw OcrError.cgImageUnavailable
    }

    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = true
    request.recognitionLanguages = ["en-US", "de-DE"]

    let handler = VNImageRequestHandler(cgImage: cgImage)
    do {
        try handler.perform([request])
    } catch {
        throw OcrError.requestFailed("Vision OCR request failed: \(error.localizedDescription)")
    }

    let imageWidth = CGFloat(cgImage.width)
    let imageHeight = CGFloat(cgImage.height)
    let observations = request.results ?? []
    let lines: [OcrLine] = observations.compactMap { observation in
        guard let candidate = observation.topCandidates(1).first else {
            return nil
        }

        let normalized = observation.boundingBox
        let rect = CGRect(
            x: normalized.origin.x * imageWidth,
            y: (1.0 - normalized.origin.y - normalized.size.height) * imageHeight,
            width: normalized.size.width * imageWidth,
            height: normalized.size.height * imageHeight)

        return OcrLine(
            text: candidate.string,
            confidence: Double(candidate.confidence),
            x: Int(rect.origin.x.rounded()),
            y: Int(rect.origin.y.rounded()),
            width: Int(rect.size.width.rounded()),
            height: Int(rect.size.height.rounded()))
    }

    return OcrPayload(
        fullText: lines.map { $0.text }.joined(separator: "\n"),
        lines: lines)
}

do {
    let configuration = try parseArguments(CommandLine.arguments)
    let payload = try recognizeText(configuration: configuration)
    let data = try JSONEncoder().encode(payload)
    FileHandle.standardOutput.write(data)
} catch {
    fputs("\(error.localizedDescription)\n", stderr)
    exit(1)
}