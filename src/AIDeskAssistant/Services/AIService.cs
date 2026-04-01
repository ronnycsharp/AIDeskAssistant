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
        For desktop application workflows such as Mail, Calendar, Word, Excel, Outlook, Finder, or Blender on macOS, prefer visible UI-based launching and focusing when possible. Use click_dock_application to open or foreground Dock apps like a human user would, then verify the app is frontmost before typing or clicking inside it.
        When a macOS-native UI element is hard to identify from screenshots alone, you may use peekaboo_inspect to inspect the current accessibility/UI structure if a local Peekaboo CLI is configured.
        On macOS, prefer the Accessibility-based tools for Apple menu items and System Settings sidebar navigation instead of coordinate-based clicks whenever those tools fit the task.
        Before typing into desktop document content on macOS, do not assume app focus is sufficient. Use focus_frontmost_window_content for the expected app, then take a screenshot and verify the caret or content area is inside the document body rather than a toolbar, ribbon, title bar, search field, or menu input.
        When entering text into editors or forms, use type_text only for literal text content. Use press_key for special keys like enter, return, tab, escape, arrows, or shortcuts. Never type words like 'enter' or 'tab' into the document unless the user explicitly asked for those literal words.
        Before typing into a desktop app document or form, explicitly ensure the correct target app is frontmost. If there is any doubt, use focus_application for that app, wait briefly, take a screenshot, and only then use type_text or press_key.
        The current screen resolution will be provided with each user request. Use it as the coordinate frame for mouse positioning together with screenshots.
        After opening an application, make sure it is actually in the foreground before typing. If needed, wait briefly, take another screenshot, and only then continue.
        Always take a screenshot first to understand the current screen state before acting.
        After each significant action, take another screenshot to confirm the result.
        Be precise with coordinates — use the screenshot to determine exact pixel positions.
        If something doesn't work, try an alternative approach.
        Explain what you are doing at each step.
        """;

    private readonly ChatClient          _client;
    private readonly DesktopToolExecutor _executor;
    private readonly AIDebugLogger?      _debugLogger;
    private readonly List<ChatMessage>   _history;

    public AIService(string apiKey, DesktopToolExecutor executor, string model = DefaultModel, AIDebugLogger? debugLogger = null)
    {
        _client   = new ChatClient(model, apiKey);
        _executor = executor;
        _debugLogger = debugLogger;
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
        string screenInfo = GetScreenInfoContext();
        string preparedUserMessage = BuildUserMessageWithScreenInfo(userMessage, screenInfo);
        _debugLogger?.LogPreparedUserMessage(preparedUserMessage);
        _history.Add(new UserChatMessage(preparedUserMessage));

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
                    if (toolName == "take_screenshot"
                        && TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment)
                        && attachment is not null)
                    {
                        _debugLogger?.LogScreenshotAttachment(toolCall.Id, attachment);
                        toolResults.Add(CreateScreenshotToolMessage(toolCall.Id, attachment));
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
                _debugLogger?.LogAssistantResponse(response);
                return response;
            }
        }
    }

    /// <summary>Clears the conversation history (keeps the system prompt).</summary>
    public void ClearHistory()
    {
        _history.RemoveRange(1, _history.Count - 1);
    }

    private string GetScreenInfoContext()
    {
        try
        {
            return _executor.Execute("get_screen_info", "{}");
        }
        catch (Exception ex)
        {
            return $"Screen information unavailable: {ex.Message}";
        }
    }

    internal static string BuildUserMessageWithScreenInfo(string userMessage, string screenInfo)
        => $"Current screen info: {screenInfo}\n\nUser task: {userMessage}";

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

    private static ToolChatMessage CreateScreenshotToolMessage(string toolCallId, ScreenshotModelAttachment attachment)
    {
        return new ToolChatMessage(
            toolCallId,
            ChatMessageContentPart.CreateTextPart(attachment.Summary),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(attachment.Bytes), attachment.MediaType, ChatImageDetailLevel.Low));
    }

    internal static bool TryParseScreenshotAttachment(string result, out ScreenshotModelAttachment? attachment)
    {
        attachment = null;
        const string base64Marker = "Base64:";
        int base64Index = result.IndexOf(base64Marker, StringComparison.Ordinal);
        if (base64Index < 0)
            return false;

        string summary = result[..base64Index].Trim();
        string base64 = result[(base64Index + base64Marker.Length)..].Trim();
        Match mediaTypeMatch = Regex.Match(summary, @"Media type:\s*(\S+)", RegexOptions.CultureInvariant);
        string mediaType = mediaTypeMatch.Success ? mediaTypeMatch.Groups[1].Value.TrimEnd('.', ',', ';') : "image/png";

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        attachment = new ScreenshotModelAttachment(summary, bytes, mediaType);
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

internal sealed record ScreenshotModelAttachment(string Summary, byte[] Bytes, string MediaType);
