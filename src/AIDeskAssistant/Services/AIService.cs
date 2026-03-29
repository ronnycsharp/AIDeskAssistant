using OpenAI;
using OpenAI.Chat;
using AIDeskAssistant.Tools;

namespace AIDeskAssistant.Services;

/// <summary>
/// Manages a multi-turn conversation with the OpenAI chat model,
/// automatically dispatching tool calls to <see cref="DesktopToolExecutor"/>.
/// </summary>
internal sealed class AIService
{
    private const string DefaultModel       = "gpt-4o";
    private const string SystemPrompt       =
        """
        You are an AI desktop assistant that can control the user's computer.
        You have access to tools that let you:
        - Take screenshots to see the current state of the screen
        - Move the mouse and click
        - Type text and press keys
        - Open applications
        - Open URLs directly in the browser
        - Wait between actions

        When the user gives you a task, figure out the necessary steps and execute them one at a time.
        Work like an agent: continue through longer multi-step tasks until the requested outcome is achieved or you are blocked.
        For browser workflows such as Gmail, web shops, or forms, prefer opening the exact URL first and then continue with screenshots, clicks, typing, and waiting as needed.
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
                    string maxRoundsMessage = "Stopped after reaching the configured maximum number of tool rounds. Ask me to continue or increase AIDESK_MAX_TOOL_ROUNDS for longer tasks.";
                    _history.Add(new AssistantChatMessage(maxRoundsMessage));
                    return maxRoundsMessage;
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

                    // For take_screenshot, send a vision-compatible message instead of plain text.
                    if (toolName == "take_screenshot" && result.Contains("Base64 PNG:"))
                    {
                        int idx = result.IndexOf("Base64 PNG:", StringComparison.Ordinal);
                        string base64 = result[(idx + "Base64 PNG:".Length)..].Trim();
                        toolResults.Add(new ToolChatMessage(toolCall.Id, $"Screenshot captured. Base64 length: {base64.Length} chars."));
                    }
                    else
                    {
                        toolResults.Add(new ToolChatMessage(toolCall.Id, result));
                    }
                }

                // Add all tool results to the history as a single round.
                foreach (var toolResult in toolResults)
                    _history.Add(toolResult);
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
}
