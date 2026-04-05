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
    private const string AssistantRole = "assistant";
    private const string TakeScreenshotToolName = "take_screenshot";
    private const string DefaultModel       = "gpt-4o";
    private const string DefaultImageMediaType = "image/png";
    private const string HistoricalScreenshotNote = "Historical screenshot image omitted from context to reduce latency. Only the latest screenshot image is retained.";
    private const string SimilarScreenshotNote = "Screenshot image omitted from history because it is visually almost unchanged compared with the previous retained screenshot. Treat repeated near-identical screenshots as evidence that the last action did not change the UI; do not repeat the same click or key sequence blindly, and switch strategy instead.";
    private const string RealtimeScreenshotOmittedNote = "Screenshot image bytes omitted from realtime tool output to keep the payload bounded.";
    private const int MaxRealtimeToolResultLength = 12_000;
    private const double ScreenshotHistorySimilarityThreshold = 0.995;
    private const string MaxToolRoundsReachedMessage =
        "Stopped after reaching the configured maximum number of tool rounds. Ask me to continue or increase AIDESK_MAX_TOOL_ROUNDS for longer tasks.";
    private const string MandatoryFinalValidationInstruction =
        "Mandatory final validation loop: do not answer the user yet. Re-verify the current desktop state from the live UI. If you changed the UI, take a fresh validation screenshot and confirm the requested outcome is actually visible now. If the task involves files or terminal output, run one concrete verification step against the current state. If validation fails, continue working instead of claiming success. You must use at least one concrete validation tool call after this instruction before you may send the final user-facing completion message.";
    private const string FinalValidationMissingEvidenceInstruction =
        "You still have not performed a concrete final verification tool call from the current live state. Do not answer the user yet. Call a validation tool now, such as take_screenshot, read_screen_text, assert_state, get_frontmost_application, get_frontmost_ui_elements, list_windows, get_focused_ui_element, wait_for_window, wait_for_ui_element, get_active_window_bounds, or run_command for file/terminal verification. Only after that validation tool result confirms the outcome may you send the final completion message.";
    private const string NegativeValidationDetectedInstructionPrefix =
        "The latest verification result showed that the requested outcome is still not confirmed. Do not answer the user with success and do not ask an optional follow-up question. Continue autonomously with the next corrective or verification step. Latest failed verification summary:";
    private const string FinalValidationAbortedMessage =
        "Stopped because the assistant attempted to finish without performing the required final validation tool call. Please ask me to continue if you want me to retry the validation step.";
    private const int MaxFinalValidationRetries = 2;
    private const string SystemPrompt =
        """
        You are an AI desktop assistant that can control the user's computer.
        Default language is German for both text and spoken interactions. Unless the user explicitly asks for another language, interpret text input and spoken input as German and respond in German.
        You have access to tools that let you:
        - Take screenshots to see the current state of the screen
        - Read visible on-screen text via native OCR on macOS for deterministic verification
        - Move the mouse and click
        - Type text and press keys
        - Open applications
        - Open URLs directly in the browser
        - Run terminal/CLI commands and read their text output
        - Move and resize the active window
        - Wait between actions

        Tool results that start with '[ERROR]' indicate that the tool call failed and the requested action was NOT performed or did NOT succeed. Do not treat an [ERROR] result as a success. When you receive an [ERROR] result, you must take corrective action: retry the step with a different approach, find an alternative path, or report the specific error to the user if no alternative exists. Never proceed to the next task step as if the previous [ERROR] result was a success.

        Use an explicit Observe -> Decide -> Act -> Reflect loop for every desktop task step.
        Observe: inspect the current state first using a screenshot, OCR, AX/UI summary, or terminal output.
        Decide: choose one concrete next action and state what exact visible change or text you expect afterward.
        Act: execute only the next tool step needed.
        Reflect: verify whether the expected change actually happened. If verification is ambiguous or incomplete, treat the step as not yet done and continue correcting.
        If you emit an internal step or planning message instead of calling a tool immediately, output a single strict JSON object and nothing else. Use this schema: {"intent":"observe|decide|act|reflect","action":{"name":"tool-or-step-name","args":{}},"expected_state":"short expected visible result","verification":"which tool or check will confirm the result"}. Do not emit free-form thought text. Your final user-facing completion message after validation should still be normal German prose, not JSON.

        When the user gives you a task, figure out the necessary steps and execute them one at a time.
        Work like an agent: continue through longer multi-step tasks until the requested outcome is achieved or you are blocked.
        In AIDesk live/realtime interactions, after each new user request, first send one short German status sentence that explains what you are going to check or do next before the first tool call. Keep it brief, user-facing, and concrete, for example that you are now checking the current state live and then fixing the issue step by step.
        After that one short live status sentence, do not send a second prose planning message. Your very next assistant turn must either call a tool immediately or emit the strict JSON planning object that leads directly to the next tool step.
        In live/realtime mode, never end the turn after a plan-only sentence. If work is required and tools are available, continue into observation or action in the same turn.
        On macOS, prefer keyboard-first approaches by default for text-heavy apps and forms. Use the mouse only when keyboard shortcuts, AX tools, OCR-guided verification, or other structured methods are insufficient for the next step.
        For browser workflows such as Gmail, web shops, or forms, prefer opening the exact URL first and then continue with screenshots, clicks, typing, and waiting as needed.
        For terminal tasks, prefer using terminal output from run_command when you need reliable text results instead of relying only on screenshots.
        For desktop application workflows such as Mail, Calendar, Word, Excel, Outlook, Finder, or Blender on macOS, prefer visible UI-based launching and focusing when possible. Use click_dock_application to open or foreground Dock apps like a human user would, then verify the app is frontmost before typing or clicking inside it.
        When a task depends on specific visible text, numbers, spreadsheet values, dialog labels, or filenames, prefer read_screen_text on macOS to verify the current state instead of inferring success from the screenshot alone.
        On macOS, prefer the Accessibility-based tools for Apple menu items and System Settings sidebar navigation instead of coordinate-based clicks whenever those tools fit the task.
        The current request may include a compact Accessibility UI summary for the frontmost macOS app, including visible roles, titles, and frames. Treat that summary as high-value structure about what is currently on screen and use it together with the screenshot.
        When a screenshot includes an additional mouse detail image around the cursor, use that close-up to validate the exact cursor position and nearby click target before choosing click or double_click coordinates.
        Before a state-changing action, take a pre-action screenshot that includes a concrete prediction of the next tool and target. Use take_screenshot with predicted_tool, predicted_action, and predicted_target_label so the screenshot result explicitly states which tool you intend to use next and which button, control, or document area you are targeting.
        If you are about to click, provide the intended target to take_screenshot so the screenshot shows an explicit click-target marker before the click.
        If you plan to click a control identified from the Accessibility UI summary, pass its frame to take_screenshot with intended_element_x, intended_element_y, intended_element_width, intended_element_height, and intended_element_label. When that AX frame is available, prefer the center of that rectangle as the click point automatically instead of inventing separate intended_click coordinates.
        When take_screenshot returns numbered marks, prefer referring to those marks by ID in the next action instead of using vague phrases like 'top left' or freshly guessed coordinates. If a relevant control or OCR line is already marked, prefer mark_id-based follow-up actions.
        Before typing into desktop document content on macOS, do not assume app focus is sufficient. Use focus_frontmost_window_content for the expected app, then take a screenshot and verify the caret or content area is inside the document body rather than a toolbar, ribbon, title bar, search field, or menu input.
        If a save dialog, open dialog, template picker, start screen, or any other modal appears, handle it explicitly before typing. Do not type through dialogs.
        For Microsoft Word and Microsoft Excel on macOS, if the task requires a new blank document or workbook, prefer press_key with 'cmd+n' after the app is frontmost instead of waiting on the start screen. Then verify with a screenshot that an editable blank document or workbook is open.
        For Microsoft Word and Microsoft Excel on macOS, do not keep sending 'cmd+n' in a loop. If one blank document or workbook is already open, use the frontmost one. Only retry 'cmd+n' when a follow-up screenshot clearly shows that no editable blank document or workbook was opened.
        For plain-text Microsoft Word document creation, full-text updates, straightforward text replacement, or simple word-level formatting on macOS, prefer the dedicated word_create_document, word_set_document_text, word_replace_text, and word_format_text tools over fragile UI navigation when those tools can satisfy the request more directly.
        Before attempting multi-step Word UI editing in realtime mode, explicitly check whether one of the dedicated Word tools can perform the requested document change more directly and more safely.
        Because the dedicated Word text tools operate on plain document text or straightforward word-level formatting, use them primarily for those direct content changes and still verify the visible result in Word afterward with a screenshot or OCR.
        For Microsoft Excel on macOS, strongly prefer keyboard-first data entry and navigation. Use shortcuts like cmd+n, cmd+home, return, tab, and arrow keys before considering mouse clicks inside the grid. After editing visible cells, use read_screen_text on the relevant grid region to verify the actual values.
        For Microsoft Word on macOS, do not declare success after typing unless a follow-up screenshot shows visible document text or the status bar word count is no longer 0. If the document still looks blank, keep troubleshooting.
        For Microsoft Word verification, if a screenshot or OCR says the page is blank, the requested text is not visible, the desired result is not confirmed, or the word count is still 0, then the task is not complete and you must continue correcting.
        For Microsoft Word text replacement on macOS, do not use the Find/Search UI as the replacement target unless the user explicitly asked for Word's search workflow. Shortcuts like cmd+f or cmd+g may highlight a match, but that is not proof that the document text itself is selected for editing.
        If a Word search field, find panel, or replace UI is open, close it or move focus back into the document body and verify with a screenshot that the caret or selection is in the document before typing, deleting, or extending a selection.
        When replacing a visible word or short phrase in Word, prefer direct document editing in the document body. Click the target line or word, validate the caret or selection on that exact document text, and only then type or press navigation keys.
        Do not extend a Word selection blindly with repeated shift+arrow presses from an unverified caret position. First establish and verify a stable anchor in the document, then make the smallest selection needed.
        If a Word replacement attempt leaves partial text, duplicated text, or a corrupted word, stop repeating the same partial edit loop. Restore a clean document anchor first, for example by undoing back to the last clean state or reselecting the exact affected line, verify the visible text, and then perform one corrected replacement.
        For text replacement or editing tasks, do not declare success unless verification shows both of these are true: the old text is gone from the relevant document location, and the new text appears in the intended document context. If the old text still appears, or the new text appears appended elsewhere, in a sidebar, in a search panel, or outside the target line/paragraph, treat the edit as failed and continue correcting.
        When OCR or screenshots include sidebars, search/replace panels, result lists, status bars, helper overlays, or other UI chrome, distinguish document content from chrome. Text that appears only in search/replace UI, a helper overlay, or another non-document panel is not proof that the document content changed.
        If a validation screenshot or OCR explicitly says that the desired result is not visible, not confirmed, or still wrong, do not send a success message in that turn.
        For Microsoft Word save tasks on macOS, do not stop after pressing Save. Wait, take another screenshot, confirm the dialog is gone, and verify the document returned to the editing window before declaring success.
        For macOS save dialogs, after changing the filename or location, do not reuse stale button coordinates. Take another screenshot, re-localize the visible Save button, and prefer passing its AX frame so the click point can be recomputed from the button center before clicking.
        If a save dialog remains open after a click, do not keep clicking nearby coordinates blindly. Re-check the button position from the latest screenshot or use a keyboard confirmation path such as press_key with enter only when the focused default button is clearly Save.
        For cursor placement in Microsoft Word, editors, and other text areas, prefer a precise mouse click at the visible insertion point when the target text is on screen. Use long arrow-key sequences only when the user explicitly requires keyboard-only positioning.
        If keyboard-only cursor placement is explicitly required, first move to a stable anchor such as the start or end of the relevant line or paragraph, take a screenshot to verify the caret moved as expected, and only then extend a selection. Do not declare success unless the follow-up screenshot clearly shows the caret or selection on the requested target text.
        When the user asks to save a document to a specific folder such as the Desktop, verify the file exists using a concrete absolute path before you stop.
        If there is a safe and obvious next diagnostic or corrective step, do not stop to ask the user for permission. Continue autonomously. Only ask a clarifying question when there is no reliable next step, multiple materially different paths are possible, or a destructive choice must be made.
        Do not end a troubleshooting turn with optional prompts such as 'Möchtest du, dass ich ...?', 'Soll ich ...?', or 'Lass uns jetzt ...', when you can already continue safely on your own.
        If you are not blocked, do not wait for a user reply. Continue with the next verification or corrective step in the same turn.
        Do not add conversational filler after a failed verification. State the problem briefly, then continue acting.
        The run_command tool does not execute a shell. Do not use shell syntax such as ~, $HOME, $(whoami), pipes, redirection, globbing, or quoted one-liners inside its arguments. If you need the home directory, first call run_command with printenv HOME and then use the returned absolute path literally in a second command.
        When entering text into editors or forms, use type_text only for literal text content. Use press_key for special keys like enter, return, tab, escape, backspace, delete, arrows, or shortcuts. Never type words like 'enter', 'tab', 'escape', 'backspace', or 'delete' into the document unless the user explicitly asked for those literal words.
        When entering multi-line text with type_text, provide real line breaks in the text value. Do not send double-escaped sequences like '\\n', '\\r', or '\\t' when the intent is an actual new line or tab inside the document.
        In text editors and document apps such as Microsoft Word, cursor navigation must always use press_key with arrow keys. Never type words like 'up', 'down', 'left', 'right', 'home', 'end', 'page up', or 'page down' into the document when the intent is to move the caret.
        In spreadsheets such as Microsoft Excel, arrow navigation must always use press_key with arrow keys. Never type words like 'up', 'down', 'left', or 'right' into a cell when the intent is to move the active selection.
        Before typing into a desktop app document or form, explicitly ensure the correct target app is frontmost. If there is any doubt, use focus_application for that app, wait briefly, take a screenshot, and only then use type_text or press_key.
        The current screen resolution will be provided with each user request. Use it as the coordinate frame for mouse positioning together with screenshots.
        After opening an application, make sure it is actually in the foreground before typing. If needed, wait briefly, take another screenshot, and only then continue.
        Always take a screenshot first to understand the current screen state before acting.
        If the task is confined to one app or one document window, prefer take_screenshot with target='active_window' so the image is smaller, cheaper, and more focused than a full-screen capture.
        Whenever you call take_screenshot, include a short purpose string that explains what the screenshot is intended to validate in the current step.
        After each significant action, take a validation screenshot to confirm the desired result. Explicitly compare the validation screenshot with the pre-action screenshot and verify that the intended UI change is now visible.
        If the validation screenshot shows that the action did not succeed, retry the action once or choose a different approach before continuing.
        If the latest validation screenshot is still almost identical to the previous one, especially around 100% similarity, treat that as a failed attempt. Do not loop on the same click, keypress, or coordinate. Change strategy, refresh focus, use a different tool, or re-observe the UI before acting again.
        Before you send any final success message after using a state-changing desktop tool, you must perform one extra final validation loop from the current live state. Take a fresh validation screenshot or another concrete verification step, confirm the requested outcome is actually present now, and only then declare completion.
        Be precise with coordinates, but prefer Accessibility/UI element frames over guessing from screenshot decorations. When you have an AX/UI element frame, use the center point of that rectangle as the click position unless the visible UI clearly indicates a different hotspot.
        For difficult focus, click, and command-triggering situations, prefer a multi-step targeting loop before the actual click or command trigger.
        Before a click, press_key, or type_text step that depends on a specific visible control or text area, call take_screenshot once with predicted_tool, predicted_action, predicted_target_label, and any intended AX target frame.
        That single take_screenshot result will include the untouched original crop first and, when a target is provided, a derived schematic target view generated from the same screenshot with the intended control emphasized.
        Compare the original screenshot and the derived schematic target view and confirm that both point to the same control before you execute the click, double_click, press_key, type_text, or other state-changing action.
        If the original screenshot and the derived schematic target view do not clearly confirm the same target, do not act yet. Re-observe, refine the target, and repeat the validation loop until the target is unambiguous.
        When using the mouse, do not assume the cursor landed correctly. Prefer active-window screenshots for targeting, check the cursor marker against the intended UI element, and validate the result with a follow-up screenshot after clicking.
        After a follow-up instruction from the AIDesk menu bar while editing Microsoft Word on macOS, assume focus may have returned to AIDesk or another helper window. Before the next edit step, explicitly bring Microsoft Word back to the foreground, focus the document content, and validate with a screenshot before typing or pressing navigation keys.
        If something doesn't work, try an alternative approach.
        Explain what you are doing at each step.
        """;

    private readonly ChatClient          _client;
    private readonly DesktopToolExecutor _executor;
    private readonly AIDebugLogger?      _debugLogger;
    private readonly string              _model;
    private string                       _thinkingLevel;
    private readonly List<ChatMessage>   _history;
    private ScreenshotModelAttachment? _latestRetainedScreenshotAttachment;
    private ScreenshotFingerprint? _latestRetainedScreenshotFingerprint;

    private readonly record struct ToolRoundOutcome(bool PerformedStateChangingTool, bool PerformedConcreteValidationTool, string? FailedValidationSummary);

    public AIService(string apiKey, DesktopToolExecutor executor, string model = DefaultModel, AIDebugLogger? debugLogger = null)
    {
        _client   = new ChatClient(model, apiKey);
        _model = model;
        _executor = executor;
        _debugLogger = debugLogger;
        _thinkingLevel = ThinkingLevelPreference.Normalize(
            Environment.GetEnvironmentVariable("AIDESK_THINKING_LEVEL")
            ?? RealtimeVoicePreferenceStore.TryLoadThinkingLevel());
        _history  = new List<ChatMessage> { new SystemChatMessage(BuildSystemPrompt()) };
        _debugLogger?.LogHistoryEntry("system", BuildSystemPrompt());
    }

    public string CurrentThinkingLevel => _thinkingLevel;

    public IReadOnlyList<string> GetAvailableThinkingLevels() => ThinkingLevelPreference.GetAvailableLevels();

    public string SetThinkingLevel(string thinkingLevel)
    {
        _thinkingLevel = ThinkingLevelPreference.Normalize(thinkingLevel);
        Environment.SetEnvironmentVariable("AIDESK_THINKING_LEVEL", _thinkingLevel);
        RealtimeVoicePreferenceStore.SaveThinkingLevel(_thinkingLevel);
        return _thinkingLevel;
    }

    public async Task<AIServiceTextResult> SendMessageWithUsageAsync(
        string userMessage,
        Action<string>? onToolCall   = null,
        Action<string>? onToolResult = null,
        int maxToolRounds            = 60,
        CancellationToken ct         = default)
    {
        string screenInfo = GetScreenInfoContext();
        string uiContext = GetFrontmostUiContext();
        string preparedUserMessage = BuildUserMessageWithScreenInfo(userMessage, screenInfo, uiContext);
        _debugLogger?.LogUiContext(uiContext);
        _debugLogger?.LogPreparedUserMessage(preparedUserMessage);
        _history.Add(new UserChatMessage(preparedUserMessage));
        _debugLogger?.LogHistoryEntry("user", preparedUserMessage);

        ChatCompletionOptions options = CreateChatCompletionOptions();

        int toolRounds = 0;
        bool performedStateChangingTool = false;
        bool finalValidationRequested = false;
        bool finalValidationEvidenceObserved = false;
        string? failedValidationSummary = null;
        int finalValidationRetryCount = 0;
        RealtimeAssistantUsage? accumulatedUsage = null;
        while (true)
        {
            ChatCompletion completion = await _client.CompleteChatAsync(_history, options, ct);
            accumulatedUsage = accumulatedUsage is null
                ? CreateUsage(completion.Usage)
                : accumulatedUsage.Add(CreateUsage(completion.Usage));

            if (completion.FinishReason != ChatFinishReason.ToolCalls)
            {
                if (!string.IsNullOrWhiteSpace(failedValidationSummary))
                {
                    string draftResponse = completion.Content[0].Text;
                    _history.Add(new AssistantChatMessage(draftResponse));
                    _debugLogger?.LogHistoryEntry(AssistantRole, $"Draft final response after failed validation: {draftResponse}");
                    _history.Add(new UserChatMessage(BuildNegativeValidationDetectedInstruction(failedValidationSummary)));
                    _debugLogger?.LogHistoryEntry("user", BuildNegativeValidationDetectedInstruction(failedValidationSummary));
                    failedValidationSummary = null;
                    continue;
                }

                if (performedStateChangingTool && !finalValidationRequested)
                {
                    finalValidationRequested = true;
                    finalValidationEvidenceObserved = false;
                    string draftResponse = completion.Content[0].Text;
                    _history.Add(new AssistantChatMessage(draftResponse));
                    _debugLogger?.LogHistoryEntry(AssistantRole, $"Draft final response before mandatory validation: {draftResponse}");
                    _history.Add(new UserChatMessage(BuildMandatoryFinalValidationInstruction()));
                    _debugLogger?.LogHistoryEntry("user", BuildMandatoryFinalValidationInstruction());
                    continue;
                }

                if (finalValidationRequested && !finalValidationEvidenceObserved)
                {
                    string draftResponse = completion.Content[0].Text;
                    _history.Add(new AssistantChatMessage(draftResponse));
                    _debugLogger?.LogHistoryEntry(AssistantRole, $"Draft final response without concrete validation evidence: {draftResponse}");

                    finalValidationRetryCount++;
                    if (finalValidationRetryCount > MaxFinalValidationRetries)
                    {
                        _history.Add(new AssistantChatMessage(FinalValidationAbortedMessage));
                        _debugLogger?.LogHistoryEntry(AssistantRole, FinalValidationAbortedMessage);
                        return new AIServiceTextResult(FinalValidationAbortedMessage, accumulatedUsage);
                    }

                    _history.Add(new UserChatMessage(BuildFinalValidationMissingEvidenceInstruction()));
                    _debugLogger?.LogHistoryEntry("user", BuildFinalValidationMissingEvidenceInstruction());
                    continue;
                }

                return FinalizeAssistantResponse(completion, accumulatedUsage);
            }

            toolRounds++;
            if (toolRounds > maxToolRounds)
            {
                _history.Add(new AssistantChatMessage(MaxToolRoundsReachedMessage));
                _debugLogger?.LogHistoryEntry("assistant", MaxToolRoundsReachedMessage);
                return new AIServiceTextResult(MaxToolRoundsReachedMessage, accumulatedUsage);
            }

            ToolRoundOutcome toolRoundOutcome = await HandleToolCallsAsync(completion, onToolCall, onToolResult);
            if (toolRoundOutcome.PerformedStateChangingTool)
            {
                performedStateChangingTool = true;
                failedValidationSummary = null;
                if (finalValidationRequested)
                    finalValidationEvidenceObserved = false;
            }

            if (toolRoundOutcome.PerformedConcreteValidationTool)
                finalValidationEvidenceObserved = true;

            if (!string.IsNullOrWhiteSpace(toolRoundOutcome.FailedValidationSummary))
            {
                finalValidationEvidenceObserved = false;
                failedValidationSummary = toolRoundOutcome.FailedValidationSummary;
            }
        }
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
        => (await SendMessageWithUsageAsync(userMessage, onToolCall, onToolResult, maxToolRounds, ct)).Text;

    internal static string BuildNegativeValidationDetectedInstruction(string validationSummary)
        => $"{NegativeValidationDetectedInstructionPrefix} {validationSummary}";

    private ChatCompletionOptions CreateChatCompletionOptions()
    {
        var options = new ChatCompletionOptions();
        foreach (ChatTool tool in DesktopToolDefinitions.GetChatTools())
            options.Tools.Add(tool);

        ThinkingLevelPreference.ApplyTo(options, _model, _thinkingLevel);

        return options;
    }

    internal static string BuildSystemPrompt()
        => SystemPrompt;

    internal static string BuildMandatoryFinalValidationInstruction()
        => MandatoryFinalValidationInstruction;

    internal static string BuildFinalValidationMissingEvidenceInstruction()
        => FinalValidationMissingEvidenceInstruction;

    internal static bool IsStateChangingTool(string toolName)
        => toolName switch
        {
            "move_mouse" => false,
            TakeScreenshotToolName => false,
            "read_screen_text" => false,
            "get_screen_info" => false,
            "get_frontmost_ui_elements" => false,
            "get_frontmost_application" => false,
            "list_windows" => false,
            "wait_for_window" => false,
            "find_ui_element" => false,
            "wait_for_ui_element" => false,
            "get_focused_ui_element" => false,
            "assert_state" => false,
            "get_cursor_position" => false,
            "get_active_window_bounds" => false,
            "wait" => false,
            "run_command" => false,
            _ => true,
        };

    internal static bool RequiresMandatoryFinalValidation(string toolName)
        => IsStateChangingTool(toolName);

    internal static bool IsConcreteValidationTool(string toolName)
        => toolName switch
        {
            TakeScreenshotToolName => true,
            "read_screen_text" => true,
            "get_frontmost_ui_elements" => true,
            "get_frontmost_application" => true,
            "list_windows" => true,
            "wait_for_window" => true,
            "find_ui_element" => true,
            "wait_for_ui_element" => true,
            "get_focused_ui_element" => true,
            "assert_state" => true,
            "get_active_window_bounds" => true,
            "run_command" => true,
            _ => false,
        };

    private async Task<ToolRoundOutcome> HandleToolCallsAsync(
        ChatCompletion completion,
        Action<string>? onToolCall,
        Action<string>? onToolResult)
    {
        _history.Add(new AssistantChatMessage(completion));
        _debugLogger?.LogHistoryEntry("assistant", $"Requested {completion.ToolCalls.Count} tool call(s): {string.Join(", ", completion.ToolCalls.Select(static call => call.FunctionName))}");

        bool performedStateChangingTool = false;
        bool performedConcreteValidationTool = false;
        string? failedValidationSummary = null;

        foreach (ChatToolCall toolCall in completion.ToolCalls)
        {
            string toolName = toolCall.FunctionName;
            string argsJson = toolCall.FunctionArguments.ToString();

            onToolCall?.Invoke($"→ Tool: {toolName}({argsJson})");

            string result = _executor.Execute(toolName, argsJson);

            onToolResult?.Invoke($"← Result: {TruncateForDisplay(result)}");

            _history.Add(CreateToolResultMessage(toolCall, toolName, result));

            if (IsStateChangingTool(toolName) && !DesktopToolExecutor.IsErrorResult(result))
            {
                performedStateChangingTool = true;
                performedConcreteValidationTool = false;
            }

            if (IsConcreteValidationTool(toolName) && !DesktopToolExecutor.IsErrorResult(result))
            {
                if (TrySummarizeFailedValidation(toolName, result, out string? validationFailureSummary))
                {
                    performedConcreteValidationTool = false;
                    failedValidationSummary = validationFailureSummary;
                }
                else
                {
                    performedConcreteValidationTool = true;
                }
            }
        }

        PruneHistoricalScreenshotImages();
        await Task.CompletedTask;
        return new ToolRoundOutcome(performedStateChangingTool, performedConcreteValidationTool, failedValidationSummary);
    }

    private ToolChatMessage CreateToolResultMessage(ChatToolCall toolCall, string toolName, string result)
    {
        if (toolName == TakeScreenshotToolName
            && TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment)
            && attachment is not null)
        {
            ToolChatMessage screenshotMessage = CreateScreenshotHistoryMessage(toolCall.Id, attachment);
            string historySummary = screenshotMessage.Content
                .Where(part => part.Kind == ChatMessageContentPartKind.Text)
                .Select(part => part.Text)
                .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text))
                ?? attachment.Summary;
            _debugLogger?.LogHistoryEntry("tool", historySummary);
            return screenshotMessage;
        }

        _debugLogger?.LogHistoryEntry("tool", result);
        return new ToolChatMessage(toolCall.Id, result);
    }

    private ToolChatMessage CreateScreenshotHistoryMessage(string toolCallId, ScreenshotModelAttachment attachment)
    {
        ScreenshotFingerprint currentFingerprint = ScreenshotHistoryComparer.CreateFingerprint(attachment.Bytes);
        double? similarity = null;

        if (_latestRetainedScreenshotFingerprint is not null)
        {
            similarity = ScreenshotHistoryComparer.CalculateSimilarity(_latestRetainedScreenshotFingerprint, currentFingerprint);
            if (similarity.Value >= ScreenshotHistorySimilarityThreshold)
            {
                _debugLogger?.LogScreenshotAttachment(toolCallId, attachment, retainedInHistory: false, similarityToPrevious: similarity);
                return new ToolChatMessage(
                    toolCallId,
                    ChatMessageContentPart.CreateTextPart($"{SimilarScreenshotNote} Similarity: {similarity.Value:P2}. {attachment.Summary}"));
            }
        }

        byte[]? differenceBytes = null;

        if (_latestRetainedScreenshotAttachment is not null)
        {
            differenceBytes = ScreenshotHistoryComparer.CreateDifferenceVisualization(_latestRetainedScreenshotAttachment.Bytes, attachment.Bytes);
            if (differenceBytes is not null)
            {
                _debugLogger?.LogScreenshotDifference(toolCallId, differenceBytes, DefaultImageMediaType, $"Visual diff against previous retained screenshot. Similarity: {(similarity.HasValue ? similarity.Value.ToString("P2") : "n/a")}");
            }
        }

        _latestRetainedScreenshotAttachment = attachment;
        _latestRetainedScreenshotFingerprint = currentFingerprint;
        _debugLogger?.LogScreenshotAttachment(toolCallId, attachment, retainedInHistory: true, similarityToPrevious: similarity);
        return CreateScreenshotToolMessage(toolCallId, attachment, differenceBytes, similarity);
    }

    private static RealtimeAssistantUsage? CreateUsage(ChatTokenUsage? usage)
    {
        if (usage is null)
            return null;

        return new RealtimeAssistantUsage(
            InputTokens: usage.InputTokenCount,
            InputTextTokens: usage.InputTokenCount,
            InputAudioTokens: usage.InputTokenDetails?.AudioTokenCount,
            InputImageTokens: null,
            CachedInputTokens: usage.InputTokenDetails?.CachedTokenCount,
            OutputTokens: usage.OutputTokenCount,
            OutputTextTokens: usage.OutputTokenCount,
            OutputAudioTokens: usage.OutputTokenDetails?.AudioTokenCount,
            TotalTokens: usage.TotalTokenCount);
    }

    private AIServiceTextResult FinalizeAssistantResponse(ChatCompletion completion, RealtimeAssistantUsage? usage)
    {
        string response = completion.Content[0].Text;
        _history.Add(new AssistantChatMessage(response));
        _debugLogger?.LogAssistantResponse(response);
        return new AIServiceTextResult(response, usage);
    }

    internal static bool TrySummarizeFailedValidation(string toolName, string result, out string? summary)
    {
        summary = null;
        if (DesktopToolExecutor.IsErrorResult(result))
            return false;

        string normalized = result.ToLowerInvariant();
        string[] negativeIndicators =
        [
            "nicht sichtbar",
            "nicht bestätigt",
            "nicht korrekt",
            "nicht vorhanden",
            "nicht im dokument",
            "seite ist leer",
            "leer sichtbar",
            "kein gedichttext",
            "kein text",
            "0 wörter",
            "bestätigt die gewünschte überprüfung nicht",
            "bestätigt die gewünschte aktion nicht",
            "task is not complete",
            "not visible",
            "not confirmed",
            "page is blank",
            "word count is still 0"
        ];

        if (!negativeIndicators.Any(normalized.Contains))
            return false;

        summary = $"{toolName}: {TruncateForDisplay(result, 280)}";
        return true;
    }

    /// <summary>Clears the conversation history (keeps the system prompt).</summary>
    public void ClearHistory()
    {
        _history.RemoveRange(1, _history.Count - 1);
        _latestRetainedScreenshotAttachment = null;
        _latestRetainedScreenshotFingerprint = null;
        _debugLogger?.LogHistoryEntry("system", "Conversation history cleared.");
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

    private string GetFrontmostUiContext()
    {
        try
        {
            return _executor.Execute("get_frontmost_ui_elements", "{}");
        }
        catch (Exception ex)
        {
            return $"Frontmost UI context unavailable: {ex.Message}";
        }
    }

    internal static string BuildUserMessageWithScreenInfo(string userMessage, string screenInfo, string? uiContext = null)
    {
        if (string.IsNullOrWhiteSpace(uiContext))
            return $"Current screen info: {screenInfo}\n\nUser task: {userMessage}";

        return $"Current screen info: {screenInfo}\n\nCurrent macOS Accessibility UI summary:\n{uiContext}\n\nUser task: {userMessage}";
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

    private static ToolChatMessage CreateScreenshotToolMessage(string toolCallId, ScreenshotModelAttachment attachment, byte[]? differenceBytes, double? similarity)
    {
        string summary = attachment.Summary;
        if (differenceBytes is not null)
        {
            summary = $"{attachment.Summary} Difference image included to show the visual change since the previous retained screenshot.";
            if (similarity.HasValue)
                summary = $"{summary} Similarity: {similarity.Value:P2}.";
        }

        if (differenceBytes is null)
        {
            var parts = new List<ChatMessageContentPart>
            {
                ChatMessageContentPart.CreateTextPart(summary),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(attachment.Bytes), attachment.MediaType, ChatImageDetailLevel.Low),
            };

            foreach (ScreenshotSupplementalImage supplementalImage in attachment.SupplementalImages)
                parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(supplementalImage.Bytes), supplementalImage.MediaType, ChatImageDetailLevel.High));

            return new ToolChatMessage(toolCallId, parts.ToArray());
        }

        var content = new List<ChatMessageContentPart>
        {
            ChatMessageContentPart.CreateTextPart(summary),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(attachment.Bytes), attachment.MediaType, ChatImageDetailLevel.Low),
        };

        foreach (ScreenshotSupplementalImage supplementalImage in attachment.SupplementalImages)
            content.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(supplementalImage.Bytes), supplementalImage.MediaType, ChatImageDetailLevel.High));

        content.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(differenceBytes), DefaultImageMediaType, ChatImageDetailLevel.Low));
        return new ToolChatMessage(toolCallId, content.ToArray());
    }

    internal static bool TryParseScreenshotAttachment(string result, out ScreenshotModelAttachment? attachment)
    {
        attachment = null;
        const string base64Marker = "Base64:";
        const string supplementalImageMarker = "Supplemental image [";
        const string mouseDetailMediaTypeMarker = "Mouse detail media type:";
        const string mouseDetailBase64Marker = "Mouse detail base64:";
        int base64Index = result.IndexOf(base64Marker, StringComparison.Ordinal);
        if (base64Index < 0)
            return false;

        string summary = result[..base64Index].Trim();
        int supplementalIndex = result.IndexOf(supplementalImageMarker, base64Index + base64Marker.Length, StringComparison.Ordinal);
        int mouseDetailMediaTypeIndex = result.IndexOf(mouseDetailMediaTypeMarker, base64Index + base64Marker.Length, StringComparison.Ordinal);
        int mouseDetailBase64Index = result.IndexOf(mouseDetailBase64Marker, base64Index + base64Marker.Length, StringComparison.Ordinal);
        int mainPayloadEndIndex = new[] { supplementalIndex, mouseDetailMediaTypeIndex, mouseDetailBase64Index }
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        string base64 = mainPayloadEndIndex >= 0
            ? result[(base64Index + base64Marker.Length)..mainPayloadEndIndex].Trim()
            : result[(base64Index + base64Marker.Length)..].Trim();
        Match mediaTypeMatch = Regex.Match(summary, @"Media type:\s*(\S+)", RegexOptions.CultureInvariant);
        string mediaType = mediaTypeMatch.Success ? mediaTypeMatch.Groups[1].Value.TrimEnd('.', ',', ';') : DefaultImageMediaType;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        var supplementalImages = new List<ScreenshotSupplementalImage>();
        if (supplementalIndex >= 0)
            supplementalImages.AddRange(ParseSupplementalImages(result[supplementalIndex..]));

        if (mouseDetailBase64Index >= 0)
        {
            string detailBase64 = result[(mouseDetailBase64Index + mouseDetailBase64Marker.Length)..].Trim();
            string detailMetadataSource = mouseDetailMediaTypeIndex >= 0 && mouseDetailBase64Index > mouseDetailMediaTypeIndex
                ? result[mouseDetailMediaTypeIndex..mouseDetailBase64Index]
                : result;
            Match detailMediaTypeMatch = Regex.Match(detailMetadataSource, @"Mouse detail media type:\s*(\S+)", RegexOptions.CultureInvariant);
            string detailMediaType = detailMediaTypeMatch.Success ? detailMediaTypeMatch.Groups[1].Value.TrimEnd('.', ',', ';') : DefaultImageMediaType;

            if (supplementalImages.All(image => !string.Equals(image.Label, "mouse-detail", StringComparison.Ordinal))
                && TryDecodeBase64(detailBase64, out byte[]? detailBytes)
                && detailBytes is not null)
            {
                supplementalImages.Add(new ScreenshotSupplementalImage("mouse-detail", detailBytes, detailMediaType));
            }
        }

        attachment = new ScreenshotModelAttachment(summary, bytes, mediaType, supplementalImages);
        return true;
    }

    private static bool TryDecodeBase64(string value, out byte[]? bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
    }

    private static IReadOnlyList<ScreenshotSupplementalImage> ParseSupplementalImages(string payload)
    {
        MatchCollection matches = Regex.Matches(
            payload,
            @"Supplemental image \[(?<label>[^\]]+)\] media type:\s*(?<mediaType>\S+?)\.\s+Supplemental image \[\k<label>\] base64:\s*(?<base64>[A-Za-z0-9+/=]+)",
            RegexOptions.CultureInvariant);

        var supplementalImages = new List<ScreenshotSupplementalImage>();
        foreach (Match match in matches.Cast<Match>().Where(static candidate => candidate.Success))
        {
            string label = match.Groups["label"].Value.Trim();
            string mediaType = match.Groups["mediaType"].Value.TrimEnd('.', ',', ';');
            string base64 = match.Groups["base64"].Value.Trim();
            if (string.IsNullOrWhiteSpace(label)
                || !TryDecodeBase64(base64, out byte[]? bytes)
                || bytes is null)
            {
                continue;
            }

            supplementalImages.Add(new ScreenshotSupplementalImage(label, bytes, string.IsNullOrWhiteSpace(mediaType) ? DefaultImageMediaType : mediaType));
        }

        return supplementalImages;
    }

    internal static string AppendScreenshotAnalysis(string result, string analysis)
    {
        if (string.IsNullOrWhiteSpace(analysis))
            return result;

        const string base64Marker = "Base64:";
        int base64Index = result.IndexOf(base64Marker, StringComparison.Ordinal);
        if (base64Index < 0)
            return $"{result}{Environment.NewLine}Vision analysis: {analysis.Trim()}";

        string summary = result[..base64Index].TrimEnd();
        string payload = result[base64Index..];
        return $"{summary} GPT-5.4 screenshot analysis: {analysis.Trim()} {payload}";
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

    internal static string CompactToolResultForRealtimeTransport(string toolName, string result)
    {
        if (string.Equals(toolName, "take_screenshot", StringComparison.Ordinal)
            && TryParseScreenshotAttachment(result, out ScreenshotModelAttachment? attachment)
            && attachment is not null)
        {
            return $"{attachment.Summary} {RealtimeScreenshotOmittedNote}";
        }

        return TruncateForRealtimeTransport(result);
    }

    private static string TruncateForRealtimeTransport(string result)
    {
        if (string.IsNullOrEmpty(result) || result.Length <= MaxRealtimeToolResultLength)
            return result;

        return $"{result[..MaxRealtimeToolResultLength]}\n… [tool result truncated to {MaxRealtimeToolResultLength} characters for realtime transport]";
    }
}

internal sealed record ScreenshotModelAttachment(string Summary, byte[] Bytes, string MediaType, IReadOnlyList<ScreenshotSupplementalImage> SupplementalImages);

internal sealed record ScreenshotSupplementalImage(string Label, byte[] Bytes, string MediaType);

internal sealed record AIServiceTextResult(string Text, RealtimeAssistantUsage? Usage);
