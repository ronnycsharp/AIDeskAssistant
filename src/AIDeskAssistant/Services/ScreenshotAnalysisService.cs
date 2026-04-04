using OpenAI.Chat;

namespace AIDeskAssistant.Services;

internal sealed class ScreenshotAnalysisService : IScreenshotAnalysisService
{
    private const string SystemPrompt =
        """
        You analyze desktop screenshots for another model that controls the UI.
        Reply in German.
        Be concise and concrete.
        Focus on what is visibly on screen, what changed, which controls or dialogs matter, and whether the screenshot appears to confirm the intended action.
        Use short plain-text sentences, not markdown, and keep the result under 900 characters.
        Mention coordinate clues only when they are useful for the next mouse action.
        Do not invent anything that is not visible in the screenshot or the screenshot summary.
        """;

    private readonly ChatClient _client;

    public ScreenshotAnalysisService(string apiKey, string model)
    {
        _client = new ChatClient(model, apiKey);
    }

    public async Task<string?> AnalyzeAsync(ScreenshotModelAttachment attachment, CancellationToken ct = default)
    {
        var parts = new List<ChatMessageContentPart>
        {
            ChatMessageContentPart.CreateTextPart($"Screenshot summary:\n{attachment.Summary}\n\nProvide a short UI analysis for the controller model."),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(attachment.Bytes), attachment.MediaType, ChatImageDetailLevel.High),
        };

        foreach (ScreenshotSupplementalImage supplementalImage in attachment.SupplementalImages)
            parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(supplementalImage.Bytes), supplementalImage.MediaType, ChatImageDetailLevel.High));

        UserChatMessage userMessage = new(parts.ToArray());

        ChatCompletion completion = await _client.CompleteChatAsync(
            [new SystemChatMessage(SystemPrompt), userMessage],
            cancellationToken: ct);

        string analysis = string.Concat(completion.Content.Select(static part => part.Text)).Trim();
        return string.IsNullOrWhiteSpace(analysis) ? null : analysis;
    }
}