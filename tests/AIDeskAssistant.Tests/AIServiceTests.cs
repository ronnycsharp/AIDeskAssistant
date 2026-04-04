using System.ClientModel;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;
using OpenAI.Chat;

namespace AIDeskAssistant.Tests;

public sealed class AIServiceTests
{
    [Fact]
    public void BuildSystemPrompt_UsesInternalScreenshotWorkflow()
    {
        string prompt = AIService.BuildSystemPrompt();

        Assert.Contains("Always take a screenshot first", prompt);
        Assert.Contains("Default language is German for both text and spoken interactions", prompt);
        Assert.Contains("target='active_window'", prompt);
        Assert.Contains("purpose string", prompt);
        Assert.Contains("After each significant action, take a validation screenshot", prompt);
        Assert.Contains("compare the validation screenshot with the pre-action screenshot", prompt);
        Assert.Contains("subtle edge ruler with labeled spacing", prompt);
        Assert.Contains("Microsoft Word and Microsoft Excel", prompt);
        Assert.Contains("In text editors and document apps such as Microsoft Word, cursor navigation must always use press_key with arrow keys", prompt);
        Assert.Contains("Never type words like 'up', 'down', 'left', 'right', 'home', 'end', 'page up', or 'page down' into the document when the intent is to move the caret", prompt);
        Assert.Contains("Use press_key for special keys like enter, return, tab, escape, backspace, delete, arrows, or shortcuts", prompt);
        Assert.Contains("Never type words like 'enter', 'tab', 'escape', 'backspace', or 'delete' into the document", prompt);
        Assert.Contains("arrow navigation must always use press_key with arrow keys", prompt);
        Assert.Contains("Never type words like 'up', 'down', 'left', or 'right' into a cell", prompt);
        Assert.Contains("press_key with 'cmd+n'", prompt);
        Assert.Contains("do not keep sending 'cmd+n' in a loop", prompt);
    }

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
    public void BuildUserMessageWithScreenInfo_IncludesUiContextWhenAvailable()
    {
        string result = AIService.BuildUserMessageWithScreenInfo(
            "Open Word and write a poem.",
            "Screen: 1920x1080, 32 bpp",
            "Frontmost app: Word\nVisible UI elements:\n- AXWindow | title=Document1 | x=0,y=25,w=1440,h=900");

        Assert.Contains("Current macOS Accessibility UI summary:", result);
        Assert.Contains("Frontmost app: Word", result);
        Assert.Contains("AXWindow | title=Document1", result);
    }

    [Fact]
    public void TryParseScreenshotAttachment_ExtractsImageBytesAndMetadata()
    {
        string result = "Screenshot taken. Target: active_window. Purpose: verify word content. Capture bounds: X=40, Y=50, Width=1280, Height=800. Corner pixels: TL=(40,50), TR=(1319,50), BL=(40,849), BR=(1319,849). Cursor: X=300, Y=400, InsideCapture=True. Resolution: 1280x800. Media type: image/jpeg. Base64: AQID";

        bool parsed = AIService.TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment);

        Assert.True(parsed);
        Assert.NotNull(attachment);
        Assert.Equal("image/jpeg", attachment.MediaType);
        Assert.Equal([1, 2, 3], attachment.Bytes);
        Assert.Contains("Corner pixels: TL=(40,50)", attachment.Summary);
        Assert.Contains("Resolution: 1280x800.", attachment.Summary);
        Assert.Contains("Purpose: verify word content.", attachment.Summary);
    }

    [Fact]
    public void TryParseScreenshotAttachment_ExtractsMouseDetailImage()
    {
        string result = "Screenshot taken. Resolution: 1280x800. Media type: image/jpeg. Mouse detail media type: image/png. Base64: AQID Mouse detail base64: BAUG";

        bool parsed = AIService.TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment);

        Assert.True(parsed);
        Assert.NotNull(attachment);
        ScreenshotSupplementalImage supplementalImage = Assert.Single(attachment.SupplementalImages);
        Assert.Equal("mouse-detail", supplementalImage.Label);
        Assert.Equal("image/png", supplementalImage.MediaType);
        Assert.Equal([4, 5, 6], supplementalImage.Bytes);
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

    [Fact]
    public void CompactToolResultForRealtimeTransport_RemovesScreenshotBase64()
    {
        string result = "Screenshot taken. Target: active_window. Capture bounds: X=40, Y=50, Width=1280, Height=800. Corner pixels: TL=(40,50), TR=(1319,50), BL=(40,849), BR=(1319,849). Cursor: X=300, Y=400, InsideCapture=True. Resolution: 1280x800. Media type: image/jpeg. Base64: AQID";

        string compacted = AIService.CompactToolResultForRealtimeTransport("take_screenshot", result);

        Assert.Contains("Capture bounds: X=40, Y=50, Width=1280, Height=800.", compacted);
        Assert.Contains("Screenshot image bytes omitted from realtime tool output", compacted);
        Assert.DoesNotContain("Base64:", compacted);
    }

    [Fact]
    public void AppendScreenshotAnalysis_InsertsAnalysisBeforeBase64Payload()
    {
        string result = "Screenshot taken. Resolution: 1280x800. Media type: image/jpeg. Base64: AQID";

        string enriched = AIService.AppendScreenshotAnalysis(result, "Fenster ist sichtbar und der Button wirkt aktiv.");

        Assert.Contains("GPT-5.4 screenshot analysis: Fenster ist sichtbar und der Button wirkt aktiv.", enriched);
        Assert.Contains("Base64: AQID", enriched);
    }

    [Fact]
    public void CompactToolResultForRealtimeTransport_TruncatesLongNonScreenshotText()
    {
        string result = new('x', 13_000);

        string compacted = AIService.CompactToolResultForRealtimeTransport("run_command", result);

        Assert.True(compacted.Length < result.Length);
        Assert.Contains("tool result truncated", compacted);
    }
}