using AIDeskAssistant.Models;

namespace AIDeskAssistant.Services;

public interface ITextRecognitionService
{
    TextRecognitionResult RecognizeText(byte[] imageBytes);
}

public readonly record struct TextRecognitionResult(string FullText, IReadOnlyList<TextRecognitionLine> Lines);

public readonly record struct TextRecognitionLine(string Text, double Confidence, WindowBounds Bounds);