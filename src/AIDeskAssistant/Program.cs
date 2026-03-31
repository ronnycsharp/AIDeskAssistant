using AIDeskAssistant;
using AIDeskAssistant.Services;
using AIDeskAssistant.Tools;

// ── Banner ──────────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("""
   ___  _____   ____            _        _            _     _              _
  / _ \|_   _| |  _ \  ___  __| | ___  / \   ___ ___(_)___| |_ __ _ _ __ | |_
 | | | | | |   | | | |/ _ \/ _` |/ _ \/ _ \ / __/ __| / __| __/ _` | '_ \| __|
 | |_| | | |   | |_| |  __/ (_| |  __/ ___ \\__ \__ \ \__ \ || (_| | | | | |_
  \__\_\ |_|   |____/ \___|\__,_|\___/_/   \_\___/___/_|___/\__\__,_|_| |_|\__|

  AI-powered desktop automation — powered by OpenAI
""");
Console.ResetColor();

// ── API Key ──────────────────────────────────────────────────────────────────
string? envFilePath = EnvironmentFileLoader.LoadFromStandardLocations();
string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write("Enter your OpenAI API key: ");
    Console.ResetColor();
    apiKey = Console.ReadLine()?.Trim() ?? string.Empty;
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

try
{
    screenshotService = PlatformServiceFactory.CreateScreenshotService();
    mouseService      = PlatformServiceFactory.CreateMouseService();
    keyboardService   = PlatformServiceFactory.CreateKeyboardService();
    terminalService   = PlatformServiceFactory.CreateTerminalService();
    windowService     = PlatformServiceFactory.CreateWindowService();
}
catch (PlatformNotSupportedException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Platform error: {ex.Message}");
    Console.ResetColor();
    return 1;
}

var executor = new DesktopToolExecutor(screenshotService, mouseService, keyboardService, terminalService, windowService);

// ── Model selection ──────────────────────────────────────────────────────────
string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
int maxToolRounds = TryGetPositiveInt(Environment.GetEnvironmentVariable("AIDESK_MAX_TOOL_ROUNDS"), 60);

var ai = new AIService(apiKey, executor, model);

// ── REPL ─────────────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Ready! Using model: {model}");
Console.WriteLine($"Agent tool rounds limit: {maxToolRounds}");
if (!string.IsNullOrWhiteSpace(envFilePath))
    Console.WriteLine($"Loaded environment from: {envFilePath}");
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
                ai.ClearHistory();
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

        string response = await ai.SendMessageAsync(
            input,
            onToolCall: msg =>
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"  {msg}");
                Console.ResetColor();
            },
            onToolResult: msg =>
            {
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
    AIDESK_MAX_TOOL_ROUNDS  Maximum agent tool rounds per task (default: 60)
    .env             Optional file in the repo root or project folder

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
