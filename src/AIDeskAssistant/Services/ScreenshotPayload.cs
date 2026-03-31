namespace AIDeskAssistant.Services;

internal sealed record ScreenshotPayload(
    byte[] Bytes,
    string MediaType,
    int OriginalByteCount,
    int FinalByteCount,
    int Width,
    int Height)
{
    public int BytesSaved => OriginalByteCount - FinalByteCount;

    public double SavingsRatio => OriginalByteCount == 0
        ? 0
        : (double)BytesSaved / OriginalByteCount;

    public string ToToolResultString()
        => $"Screenshot taken. Original: {OriginalByteCount} bytes. Final: {FinalByteCount} bytes. Saved: {BytesSaved} bytes ({SavingsRatio:P1}). Resolution: {Width}x{Height}. Media type: {MediaType}. Base64: {Convert.ToBase64String(Bytes)}";
}