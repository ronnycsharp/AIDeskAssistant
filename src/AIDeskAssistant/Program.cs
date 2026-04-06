using AIDeskAssistant;
using AIDeskAssistant.Mcp;
using AIDeskAssistant.Platform.MacOS;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

bool menuBarStatusRequested = args.Contains("--menu-bar-status", StringComparer.OrdinalIgnoreCase);
bool menuBarStopRequested = args.Contains("--menu-bar-stop", StringComparer.OrdinalIgnoreCase);
bool mcpRequested = args.Contains("--mcp", StringComparer.OrdinalIgnoreCase);

if (menuBarStatusRequested)
{
    PrintMenuBarStatus(MenuBarRuntimeState.GetStatus());
    return 0;
}

if (menuBarStopRequested)
{
    bool stopped = MenuBarRuntimeState.TryStopRunningHost(out string message);
    Console.ForegroundColor = stopped ? ConsoleColor.Green : ConsoleColor.Yellow;
    Console.WriteLine(message);
    Console.ResetColor();
    return stopped ? 0 : 1;
}

// ── MCP server mode ──────────────────────────────────────────────────────────
// In MCP mode stdout is owned by the protocol – suppress the banner and skip the
// interactive REPL.  All diagnostic output goes to stderr.
if (mcpRequested)
{
    string? mcpEnvFilePath = EnvironmentFileLoader.LoadFromStandardLocations();

    // --api-key <value> can override the environment variable.
    string? cliApiKey = GetNamedArg(args, "--api-key");
    if (!string.IsNullOrWhiteSpace(cliApiKey))
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", cliApiKey);

    string apiKeyMcp = LanguagePreferenceStore.TryLoadApiKey() ?? string.Empty;
    if (string.IsNullOrWhiteSpace(apiKeyMcp))
    {
        Console.Error.WriteLine("[AIDeskAssistant MCP] No API key found. " +
            "Set OPENAI_API_KEY in the environment or pass --api-key <key>.");
        return 1;
    }

    IScreenshotService screenshotServiceMcp;
    IMouseService      mouseServiceMcp;
    IKeyboardService   keyboardServiceMcp;
    ITerminalService   terminalServiceMcp;
    IWindowService     windowServiceMcp;
    IUiAutomationService uiAutomationServiceMcp;
    ITextRecognitionService textRecognitionServiceMcp;

    try
    {
        screenshotServiceMcp    = PlatformServiceFactory.CreateScreenshotService();
        mouseServiceMcp         = PlatformServiceFactory.CreateMouseService();
        keyboardServiceMcp      = PlatformServiceFactory.CreateKeyboardService();
        terminalServiceMcp      = PlatformServiceFactory.CreateTerminalService();
        windowServiceMcp        = PlatformServiceFactory.CreateWindowService();
        uiAutomationServiceMcp  = PlatformServiceFactory.CreateUiAutomationService();
        textRecognitionServiceMcp = PlatformServiceFactory.CreateTextRecognitionService();
    }
    catch (PlatformNotSupportedException ex)
    {
        Console.Error.WriteLine($"[AIDeskAssistant MCP] Platform error: {ex.Message}");
        return 1;
    }

    var executorMcp = new DesktopToolExecutor(
        screenshotServiceMcp, mouseServiceMcp, keyboardServiceMcp,
        terminalServiceMcp, windowServiceMcp, uiAutomationServiceMcp,
        textRecognitionServiceMcp);

    Console.Error.WriteLine($"[AIDeskAssistant MCP] Starting MCP server (stdio). Language: {LanguagePreferenceStore.CurrentDisplayName} ({LanguagePreferenceStore.Current}).");
    if (!string.IsNullOrWhiteSpace(mcpEnvFilePath))
        Console.Error.WriteLine($"[AIDeskAssistant MCP] Loaded environment from: {mcpEnvFilePath}");

    return await McpServerRunner.RunAsync(executorMcp, args);
}

// ── Banner ──────────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""
            _    ___ ____            _      ____            _        _              _
         / \  |_ _|  _ \  ___  ___| | __ / ___|___  _ __ | |_ _ __| |__   ___  __| |
        / _ \  | || | | |/ _ \/ __| |/ /| |   / _ \| '_ \| __| '__| '_ \ / _ \/ _` |
     / ___ \ | || |_| |  __/\__ \   < | |__| (_) | |_) | |_| |  | |_) |  __/ (_| |
    /_/   \_\___|____/ \___||___/_|\_\\____\___/| .__/ \__|_|  |_.__/ \___|\__,_|
                                                                                            |_|

                                 Desktop automation with vision, tools, and native UI control
""");
Console.ResetColor();

// ── API Key ──────────────────────────────────────────────────────────────────
string? envFilePath = EnvironmentFileLoader.LoadFromStandardLocations();
string apiKey = LanguagePreferenceStore.TryLoadApiKey() ?? string.Empty;

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write("Enter your OpenAI API key: ");
    Console.ResetColor();
    apiKey = Console.ReadLine()?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(apiKey))
        LanguagePreferenceStore.SaveApiKey(apiKey);
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("No API key provided. Set the OPENAI_API_KEY environment variable or enter it at the prompt.");
    Console.ResetColor();
    return 1;
}

// ── Platform services ────────────────────────────────────────────────────────
IScreenshotService screenshotService;
IMouseService      mouseService;
IKeyboardService   keyboardService;
ITerminalService   terminalService;
IWindowService     windowService;
IUiAutomationService uiAutomationService;
ITextRecognitionService textRecognitionService;

try
{
    screenshotService = PlatformServiceFactory.CreateScreenshotService();
    mouseService      = PlatformServiceFactory.CreateMouseService();
    keyboardService   = PlatformServiceFactory.CreateKeyboardService();
    terminalService   = PlatformServiceFactory.CreateTerminalService();
    windowService     = PlatformServiceFactory.CreateWindowService();
    uiAutomationService = PlatformServiceFactory.CreateUiAutomationService();
    textRecognitionService = PlatformServiceFactory.CreateTextRecognitionService();
}
catch (PlatformNotSupportedException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Platform error: {ex.Message}");
    Console.ResetColor();
    return 1;
}

var executor = new DesktopToolExecutor(screenshotService, mouseService, keyboardService, terminalService, windowService, uiAutomationService, textRecognitionService);
var debugLogger = AIDebugLogger.CreateFromArgsAndEnvironment(args);

// ── Model selection ──────────────────────────────────────────────────────────
string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
string realtimeModel = Environment.GetEnvironmentVariable("OPENAI_REALTIME_MODEL") ?? "gpt-realtime";
int maxToolRounds = TryGetPositiveInt(Environment.GetEnvironmentVariable("AIDESK_MAX_TOOL_ROUNDS"), 60);

bool menuBarRequested = args.Contains("--menu-bar", StringComparer.OrdinalIgnoreCase);
bool menuBarHostRequested = args.Contains("--menu-bar-host", StringComparer.OrdinalIgnoreCase);

if (menuBarRequested && !menuBarHostRequested)
{
    if (!OperatingSystem.IsMacOS())
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("The --menu-bar mode is only available on macOS.");
        Console.ResetColor();
        return 1;
    }

    MenuBarRuntimeStatus existingStatus = MenuBarRuntimeState.GetStatus();
    if (existingStatus.IsRunning)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Menu bar host is already running (PID {existingStatus.ProcessId}).");
        if (!string.IsNullOrWhiteSpace(existingStatus.ServerUri))
            Console.WriteLine($"Server URI: {existingStatus.ServerUri}");
        Console.WriteLine($"Status file: {existingStatus.StatusFilePath}");
        Console.ResetColor();
        return 0;
    }

    MacOSStatusBarLauncher.LaunchDetachedHost(args);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Menu bar host started in the background.");
    Console.WriteLine("Use the macOS menu bar icon to interact with AIDeskAssistant.");
    if (!string.IsNullOrWhiteSpace(envFilePath))
        Console.WriteLine($"Loaded environment from: {envFilePath}");
    Console.ResetColor();
    return 0;
}

if (menuBarHostRequested)
{
    if (!OperatingSystem.IsMacOS())
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("The --menu-bar-host mode is only available on macOS.");
        Console.ResetColor();
        return 1;
    }

    debugLogger ??= AIDebugLogger.CreateFromArgsAndEnvironment(args, forceEnabled: true);
    IScreenshotAnalysisService screenshotAnalysisService = new ScreenshotAnalysisService(apiKey, model);

    await using var realtimeAssistant = new RealtimeAssistantService(apiKey, executor, realtimeModel, debugLogger, screenshotAnalysisService);
    await using var server = new RealtimeMenuBarServer(realtimeAssistant);
    await server.StartAsync();
    MenuBarRuntimeState.RegisterCurrentProcess(server.BaseUri);

    try
    {
        if (debugLogger is not null)
        {
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_TOOL_LOG_FILE", debugLogger.ToolActivityFilePath);
            Environment.SetEnvironmentVariable("AIDESK_MENU_BAR_DEBUG_SESSION_DIR", debugLogger.SessionDirectoryPath);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Menu bar mode ready on {server.BaseUri}");
        Console.WriteLine($"Realtime speech + tool model: {realtimeModel}");
        Console.WriteLine($"Screenshot analysis model: {model}");
        Console.WriteLine($"Status file: {MenuBarRuntimeState.StatusFilePath}");
        if (!string.IsNullOrWhiteSpace(envFilePath))
            Console.WriteLine($"Loaded environment from: {envFilePath}");
        if (debugLogger is not null)
            Console.WriteLine($"AI debug mode enabled. Session logs: {debugLogger.SessionDirectoryPath}");
        Console.ResetColor();

        int exitCode = await MacOSStatusBarLauncher.RunAsync(server.BaseUri);
        return exitCode;
    }
    finally
    {
        MenuBarRuntimeState.ClearIfOwnedByCurrentProcess();
    }
}

var chatAssistant = new AIService(apiKey, executor, model, debugLogger);

// ── REPL ─────────────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Ready! Using model: {model}");
Console.WriteLine($"Language: {LanguagePreferenceStore.CurrentDisplayName} ({LanguagePreferenceStore.Current}) — change with AIDESK_LANGUAGE env var");
Console.WriteLine($"Agent tool rounds limit: {maxToolRounds}");
if (!string.IsNullOrWhiteSpace(envFilePath))
    Console.WriteLine($"Loaded environment from: {envFilePath}");
if (debugLogger is not null)
    Console.WriteLine($"AI debug mode enabled. Session logs: {debugLogger.SessionDirectoryPath}");
Console.WriteLine("Type your command, or use /help for available commands.");
Console.ResetColor();
Console.WriteLine();

while (true)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("You> ");
    Console.ResetColor();

    string? input = Console.ReadLine();
    if (input is null) break; // EOF / Ctrl+D

    input = input.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    // ── Built-in commands ────────────────────────────────────────────────────
    if (input.StartsWith('/'))
    {
        switch (input.ToLowerInvariant())
        {
            case "/help":
                PrintHelp();
                continue;

            case "/clear":
            case "/reset":
                chatAssistant.ClearHistory();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Conversation history cleared.");
                Console.ResetColor();
                continue;

            case "/quit":
            case "/exit":
            case "/q":
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Goodbye!");
                Console.ResetColor();
                return 0;

            default:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Unknown command: {input}. Type /help for help.");
                Console.ResetColor();
                continue;
        }
    }

    // ── Send to AI ───────────────────────────────────────────────────────────
    try
    {
        using var cts = new CancellationTokenSource();

        // Ctrl+C cancels the current request without exiting.
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nRequest cancelled.");
                Console.ResetColor();
            }
        };

        string response = await chatAssistant.SendMessageAsync(
            input,
            onToolCall: msg =>
            {
                debugLogger?.LogToolCall(msg);
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"  {msg}");
                Console.ResetColor();
            },
            onToolResult: msg =>
            {
                debugLogger?.LogToolResult(msg);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {msg}");
                Console.ResetColor();
            },
            maxToolRounds: maxToolRounds,
            ct: cts.Token
        );

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("AI> ");
        Console.ResetColor();
        Console.WriteLine(response);
        Console.WriteLine();
    }
    catch (OperationCanceledException)
    {
        // Already printed message in the event handler.
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }
}

return 0;

// ── Help ──────────────────────────────────────────────────────────────────────
static void PrintHelp()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""

    AIDeskAssistant — CLI Commands
    ─────────────────────────────────────────────────────────────────────────────
    /help          Show this help text
    /clear, /reset Clear the conversation history (start a new context)
    /quit, /exit   Exit the application
    Ctrl+C         Cancel the current AI request

    Environment Variables
    ─────────────────────────────────────────────────────────────────────────────
    OPENAI_API_KEY   Your OpenAI API key (required)
    OPENAI_MODEL     Model to use (default: gpt-4o)
    OPENAI_REALTIME_MODEL  Model to use for --menu-bar mode (default: gpt-realtime)
    AIDESK_MAX_TOOL_ROUNDS  Maximum agent tool rounds per task (default: 60)
    AIDESK_LANGUAGE  Interaction language: 'de' (German, default) or 'en' (English)
    AIDESK_DEBUG_MODEL_IO  Enable model I/O debug logging (1/true/yes/on)
    AIDESK_DEBUG_DIR  Optional directory for debug sessions (default: ./.aidesk-debug)
    AIDESK_MENU_BAR_STATUS_FILE  Optional path for menu bar status tracking
    .env             Optional file in the repo root or project folder

    Startup Modes
    ─────────────────────────────────────────────────────────────────────────────
    --mcp            Start as an MCP server (stdio) for VS Code / Claude Desktop
    --api-key <key>  Provide the OpenAI API key as a command-line argument (MCP mode)
    --menu-bar       Start the macOS menu bar assistant in the background
    --menu-bar-host  Internal foreground host used by --menu-bar
    --menu-bar-status  Show whether the macOS menu bar host is currently running
    --menu-bar-stop  Stop the currently running macOS menu bar host
    --debug-model-io Enable model I/O debug logging for this CLI run

    MCP Configuration (VS Code mcp.json / Claude Desktop config)
    ─────────────────────────────────────────────────────────────────────────────
    Example VS Code .vscode/mcp.json entry:
    {
      "servers": {
        "aidesk": {
          "type": "stdio",
          "command": "dotnet",
          "args": ["run", "--project", "<path-to-AIDeskAssistant.csproj>", "--", "--mcp"],
          "env": {
            "OPENAI_API_KEY": "sk-...",
            "AIDESK_LANGUAGE": "de"  // "de" = German (default), "en" = English
          }
        }
      }
    }

    MCP Tools exposed in --mcp mode
    ─────────────────────────────────────────────────────────────────────────────
    Desktop:  take_screenshot, click, type_text, move_mouse, press_key,
              open_application, run_command, get_frontmost_ui_elements, ...
    Speech:   speak_text, get_voices, set_voice
    Config:   set_language, get_language, get_config

    Example Commands
    ─────────────────────────────────────────────────────────────────────────────
    Open Safari and navigate to https://example.com
    Open Gmail and draft an email to xyz@example.com
    Run git status in the terminal and summarize the output
    Move the active window to x 40 y 40 and resize it to 1200 by 800
    Search for "cat videos" on YouTube
    Open Notepad and type Hello World
    Take a screenshot and describe what you see
    Click on the Start button and open the Settings app

    Notes
    ─────────────────────────────────────────────────────────────────────────────
    Mouse movement is smoothed with easing for more natural cursor motion
    ─────────────────────────────────────────────────────────────────────────────
    """);
    Console.ResetColor();
}

/// <summary>Parses a positive integer environment variable or returns a fallback value.</summary>
static int TryGetPositiveInt(string? value, int defaultValue)
    => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;

/// <summary>Returns the value following a named argument, e.g. "--api-key sk-xxx".</summary>
static string? GetNamedArg(string[] arguments, string name)
{
    for (int i = 0; i < arguments.Length - 1; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
            return arguments[i + 1];
    }
    return null;
}

static void PrintMenuBarStatus(MenuBarRuntimeStatus status)
{
    Console.ForegroundColor = status.IsRunning ? ConsoleColor.Green : ConsoleColor.Yellow;
    Console.WriteLine(status.IsRunning ? "Menu bar host is running." : "Menu bar host is not running.");
    Console.ResetColor();

    if (status.ProcessId is not null)
        Console.WriteLine($"PID: {status.ProcessId}");

    if (!string.IsNullOrWhiteSpace(status.ServerUri))
        Console.WriteLine($"Server URI: {status.ServerUri}");

    if (status.StartedAtUtc is not null)
        Console.WriteLine($"Started (UTC): {status.StartedAtUtc:O}");

    Console.WriteLine($"Status file: {status.StatusFilePath}");

    if (!string.IsNullOrWhiteSpace(status.Detail))
        Console.WriteLine($"Detail: {status.Detail}");
}
