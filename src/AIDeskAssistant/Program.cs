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

try
{
    screenshotService = PlatformServiceFactory.CreateScreenshotService();
    mouseService      = PlatformServiceFactory.CreateMouseService();
    keyboardService   = PlatformServiceFactory.CreateKeyboardService();
}
catch (PlatformNotSupportedException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"Platform error: {ex.Message}");
    Console.ResetColor();
    return 1;
}

var executor = new DesktopToolExecutor(screenshotService, mouseService, keyboardService);

// ── Model selection ──────────────────────────────────────────────────────────
string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";

var ai = new AIService(apiKey, executor, model);

// ── REPL ─────────────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Ready! Using model: {model}");
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

    Example Commands
    ─────────────────────────────────────────────────────────────────────────────
    Open Safari and navigate to https://example.com
    Search for "cat videos" on YouTube
    Open Notepad and type Hello World
    Take a screenshot and describe what you see
    Click on the Start button and open the Settings app
    ─────────────────────────────────────────────────────────────────────────────
    """);
    Console.ResetColor();
}
