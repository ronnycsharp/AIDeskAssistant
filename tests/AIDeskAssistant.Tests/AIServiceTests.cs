using System.ClientModel;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;
using OpenAI.Chat;

namespace AIDeskAssistant.Tests;

public sealed class AIServiceTests
{
    [Fact]
    public void BuildUserMessageWithScreenInfo_PrependsScreenContext()
    {
        string result = AIService.BuildUserMessageWithScreenInfo(
            "Open Word and write a poem.",
            "Screen: 1920x1080, 32 bpp");

        Assert.Contains("Current screen info: Screen: 1920x1080, 32 bpp", result);
        Assert.Contains("User task: Open Word and write a poem.", result);
    }

    [Fact]
    public void TryParseScreenshotAttachment_ExtractsImageBytesAndMetadata()
    {
        string result = "Screenshot taken. Resolution: 1280x800. Media type: image/jpeg. Base64: AQID";

        bool parsed = AIService.TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment);

        Assert.True(parsed);
        Assert.NotNull(attachment);
        Assert.Equal("image/jpeg", attachment.MediaType);
        Assert.Equal([1, 2, 3], attachment.Bytes);
        Assert.Contains("Resolution: 1280x800.", attachment.Summary);
    }

    [Fact]
    public void TryCompactScreenshotToolMessage_ReplacesHistoricalImageWithTextOnlyMessage()
    {
        ToolChatMessage original = new(
            "call-1",
            ChatMessageContentPart.CreateTextPart("Screenshot taken. Original: 100 bytes. Final: 50 bytes."),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes([1, 2, 3]), "image/jpeg", ChatImageDetailLevel.Low));

        bool compacted = AIService.TryCompactScreenshotToolMessage(original, retainImage: false, out ToolChatMessage replacement);

        Assert.True(compacted);
        Assert.Single(replacement.Content);
        Assert.Equal(ChatMessageContentPartKind.Text, replacement.Content[0].Kind);
        Assert.Contains("Historical screenshot image omitted", replacement.Content[0].Text);
        Assert.Equal("call-1", replacement.ToolCallId);
    }

    [Fact]
    public void TryCompactScreenshotToolMessage_KeepsLatestScreenshotImage()
    {
        ToolChatMessage original = new(
            "call-2",
            ChatMessageContentPart.CreateTextPart("Screenshot taken. Original: 100 bytes. Final: 50 bytes."),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes([1, 2, 3]), "image/jpeg", ChatImageDetailLevel.Low));

        bool compacted = AIService.TryCompactScreenshotToolMessage(original, retainImage: true, out ToolChatMessage replacement);

        Assert.True(compacted);
        Assert.Equal(2, replacement.Content.Count);
        Assert.Contains(replacement.Content, part => part.Kind == ChatMessageContentPartKind.Image);
    }

    [Fact]
    public void TryCompactScreenshotToolMessage_IgnoresNonScreenshotToolMessages()
    {
        ToolChatMessage original = new("call-3", "Command exited with code 0.");

        bool compacted = AIService.TryCompactScreenshotToolMessage(original, retainImage: false, out ToolChatMessage replacement);

        Assert.False(compacted);
        Assert.Same(original, replacement);
    }
}