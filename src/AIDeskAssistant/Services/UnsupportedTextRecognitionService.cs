namespace AIDeskAssistant.Services;

internal sealed class UnsupportedTextRecognitionService : ITextRecognitionService
{
    private readonly string _platformName;

    public UnsupportedTextRecognitionService(string platformName)
    {
        _platformName = platformName;
    }

    public TextRecognitionResult RecognizeText(byte[] imageBytes)
        => throw new PlatformNotSupportedException($"Native OCR is not available on {_platformName}.");
}