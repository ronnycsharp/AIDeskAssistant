using OpenAI;
using OpenAI.Chat;
using AIDeskAssistant.Tools;
using System.Text.RegularExpressions;

namespace AIDeskAssistant.Services;

/// <summary>
/// Manages a multi-turn conversation with the OpenAI chat model,
/// automatically dispatching tool calls to <see cref="DesktopToolExecutor"/>.
/// </summary>
internal sealed class AIService
{
    private const string DefaultModel       = "gpt-4o";
    private const string HistoricalScreenshotNote = "Historical screenshot image omitted from context to reduce latency. Only the latest screenshot image is retained.";
    private const string MaxToolRoundsReachedMessage =
        "Stopped after reaching the configured maximum number of tool rounds. Ask me to continue or increase AIDESK_MAX_TOOL_ROUNDS for longer tasks.";
    private const string SystemPrompt       =
        """
        You are an AI desktop assistant that can control the user's computer.
        You have access to tools that let you:
        - Take screenshots to see the current state of the screen
        - Move the mouse and click
        - Type text and press keys
        - Open applications
        - Open URLs directly in the browser
        - Run terminal/CLI commands and read their text output
        - Move and resize the active window
        - Wait between actions

        When the user gives you a task, figure out the necessary steps and execute them one at a time.
        Work like an agent: continue through longer multi-step tasks until the requested outcome is achieved or you are blocked.
        For browser workflows such as Gmail, web shops, or forms, prefer opening the exact URL first and then continue with screenshots, clicks, typing, and waiting as needed.
        For terminal tasks, prefer using terminal output from run_command when you need reliable text results instead of relying only on screenshots.
        On macOS, prefer the Accessibility-based tools for Apple menu items and System Settings sidebar navigation instead of coordinate-based clicks whenever those tools fit the task.
        Always take a screenshot first to understand the current screen state before acting.
        After each significant action, take another screenshot to confirm the result.
        Be precise with coordinates — use the screenshot to determine exact pixel positions.
        If something doesn't work, try an alternative approach.
        Explain what you are doing at each step.
        """;

    private readonly ChatClient          _client;
    private readonly DesktopToolExecutor _executor;
    private readonly List<ChatMessage>   _history;

    public AIService(string apiKey, DesktopToolExecutor executor, string model = DefaultModel)
    {
        _client   = new ChatClient(model, apiKey);
        _executor = executor;
        _history  = new List<ChatMessage> { new SystemChatMessage(SystemPrompt) };
    }

    /// <summary>
    /// Sends a user message and runs the tool-calling loop until the model
    /// produces a final text response. Returns the assistant's final message.
    /// </summary>
    public async Task<string> SendMessageAsync(
        string userMessage,
        Action<string>? onToolCall   = null,
        Action<string>? onToolResult = null,
        int maxToolRounds            = 60,
        CancellationToken ct         = default)
    {
        _history.Add(new UserChatMessage(userMessage));

        var options = new ChatCompletionOptions();
        foreach (var tool in DesktopToolDefinitions.All)
            options.Tools.Add(tool);

        int toolRounds = 0;
        while (true)
        {
            ChatCompletion completion = await _client.CompleteChatAsync(_history, options, ct);

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                toolRounds++;
                if (toolRounds > maxToolRounds)
                {
                    _history.Add(new AssistantChatMessage(MaxToolRoundsReachedMessage));
                    return MaxToolRoundsReachedMessage;
                }

                // Add the assistant's tool-call message to history.
                _history.Add(new AssistantChatMessage(completion));

                // Execute each tool and collect results.
                var toolResults = new List<ToolChatMessage>();
                foreach (ChatToolCall toolCall in completion.ToolCalls)
                {
                    string toolName = toolCall.FunctionName;
                    string argsJson = toolCall.FunctionArguments.ToString();

                    onToolCall?.Invoke($"→ Tool: {toolName}({argsJson})");

                    string result = _executor.Execute(toolName, argsJson);

                    onToolResult?.Invoke($"← Result: {TruncateForDisplay(result)}");

                    // For take_screenshot, attach the actual image so the model can see the screen.
                    if (toolName == "take_screenshot" && TryCreateScreenshotToolMessage(toolCall.Id, result, out ToolChatMessage? screenshotToolMessage) && screenshotToolMessage is not null)
                    {
                        toolResults.Add(screenshotToolMessage);
                    }
                    else
                    {
                        toolResults.Add(new ToolChatMessage(toolCall.Id, result));
                    }
                }

                // Add all tool results to the history as a single round.
                foreach (var toolResult in toolResults)
                    _history.Add(toolResult);

                PruneHistoricalScreenshotImages();
            }
            else
            {
                // Final text response.
                string response = completion.Content[0].Text;
                _history.Add(new AssistantChatMessage(response));
                return response;
            }
        }
    }

    /// <summary>Clears the conversation history (keeps the system prompt).</summary>
    public void ClearHistory()
    {
        _history.RemoveRange(1, _history.Count - 1);
    }

    private static string TruncateForDisplay(string s, int maxLength = 120)
        => s.Length <= maxLength ? s : s[..maxLength] + "…";

    private void PruneHistoricalScreenshotImages()
    {
        bool latestScreenshotRetained = false;

        for (int index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index] is not ToolChatMessage toolMessage)
                continue;

            if (!TryCompactScreenshotToolMessage(toolMessage, retainImage: !latestScreenshotRetained, out ToolChatMessage? replacement))
                continue;

            if (!latestScreenshotRetained)
            {
                latestScreenshotRetained = true;
                continue;
            }

            _history[index] = replacement;
        }
    }

    private static bool TryCreateScreenshotToolMessage(string toolCallId, string result, out ToolChatMessage? message)
    {
        message = null;

        const string base64Marker = "Base64:";
        int base64Index = result.IndexOf(base64Marker, StringComparison.Ordinal);
        if (base64Index < 0)
            return false;

        string summary = result[..base64Index].Trim();
        string base64 = result[(base64Index + base64Marker.Length)..].Trim();
        Match mediaTypeMatch = Regex.Match(summary, @"Media type:\s*(\S+)", RegexOptions.CultureInvariant);
        string mediaType = mediaTypeMatch.Success ? mediaTypeMatch.Groups[1].Value : "image/png";

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        message = new ToolChatMessage(
            toolCallId,
            ChatMessageContentPart.CreateTextPart(summary),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mediaType, ChatImageDetailLevel.Low));
        return true;
    }

    internal static bool TryCompactScreenshotToolMessage(ToolChatMessage toolMessage, bool retainImage, out ToolChatMessage replacement)
    {
        ChatMessageContentPart[] contentParts = toolMessage.Content.ToArray();
        bool containsImage = contentParts.Any(part => part.Kind == ChatMessageContentPartKind.Image);

        if (!containsImage)
        {
            replacement = toolMessage;
            return false;
        }

        if (retainImage)
        {
            replacement = toolMessage;
            return true;
        }

        string summary = contentParts
            .Where(part => part.Kind == ChatMessageContentPartKind.Text)
            .Select(part => part.Text)
            .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text))
            ?? "Screenshot captured earlier.";

        replacement = new ToolChatMessage(
            toolMessage.ToolCallId,
            ChatMessageContentPart.CreateTextPart($"{HistoricalScreenshotNote} {summary}"));
        return true;
    }
}
