using System.ClientModel;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;
using OpenAI.Chat;

namespace AIDeskAssistant.Tests;

public sealed class AIServiceTests
{
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