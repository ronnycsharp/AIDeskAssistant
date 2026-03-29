# AIDeskAssistant

An AI-powered desktop automation assistant for Windows and macOS, built with C# (.NET 8) and the OpenAI API. Give natural-language instructions and the AI controls your computer — taking screenshots, moving the mouse, clicking, typing, launching applications, opening browser URLs for longer web workflows like Gmail, running CLI commands, and managing the active window.

## Features

- 🤖 **LLM-powered** — uses OpenAI GPT-4o (or any chat model) with function/tool calling
- 📸 **Screen vision** — takes screenshots so the AI can see what's on screen
- 🖱️ **Mouse control** — move, click (left/right/middle), double-click, scroll
- ✨ **Natural cursor motion** — mouse moves along a smooth eased path instead of teleporting instantly
- ⌨️ **Keyboard input** — type text, press key combinations (Ctrl+C, Cmd+Space, …)
- 🚀 **App launcher** — open any installed application by name
- 🌐 **Direct URL navigation** — open Gmail, shops, forms, and other sites in the default browser
- 🖥️ **Terminal command execution** — run CLI tools and feed stdout/stderr back to the model
- 🪟 **Window management** — inspect, move, and resize the active window
- 💬 **CLI interface** — a simple REPL where you speak to the AI in plain language
- 🔄 **Agentic loop** — the AI keeps using tools until the task is done, then reports back
- ⏱️ **Longer task support** — configurable max tool rounds for multi-step browser tasks

## Requirements

| Platform | Runtime | Notes |
|----------|---------|-------|
| Windows  | .NET 8+ | Uses Win32 API (user32, gdi32) |
| macOS    | .NET 8+ | Uses CoreGraphics + `screencapture`/`osascript` |

## Getting Started

### 1. Clone & build

```bash
git clone https://github.com/ronnycsharp/AIDeskAssistant.git
cd AIDeskAssistant
dotnet build
```

### 2. Set your API key

```bash
# Linux / macOS
export OPENAI_API_KEY="sk-..."

# Windows PowerShell
$env:OPENAI_API_KEY = "sk-..."
```

Or you will be prompted for it on first run.

### 3. Run

```bash
dotnet run --project src/AIDeskAssistant
```

Optional:

```bash
export AIDESK_MAX_TOOL_ROUNDS=120
```

### 4. Give it a task

```
You> Open Safari and navigate to https://example.com
You> Open Gmail and draft an email to xyz@example.com
You> Run git status and tell me what changed
You> Move the active window to x 50 y 50 and resize it to 1280 by 800
You> Search for "cat videos" on YouTube and click the first result
You> Open Notepad and write a short poem about computers
You> Take a screenshot and describe what you see
You> /help
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `/help`           | Show help text |
| `/clear`, `/reset`| Clear conversation history |
| `/quit`, `/exit`  | Exit the program |
| `Ctrl+C`          | Cancel the current AI request |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `OPENAI_API_KEY` | *(required)* | Your OpenAI API key |
| `OPENAI_MODEL`   | `gpt-4o`     | OpenAI model to use |
| `AIDESK_MAX_TOOL_ROUNDS` | `60` | Maximum agent tool rounds per task before the assistant stops and asks to continue |

## Available Desktop Tools

The following tools are exposed to the AI model:

| Tool | Description |
|------|-------------|
| `take_screenshot`    | Full-screen screenshot (PNG, base64) |
| `get_screen_info`    | Screen width, height, bit depth |
| `get_cursor_position`| Current mouse cursor position |
| `move_mouse`         | Move cursor to (x, y) |
| `click`              | Click left/right/middle button at (x, y) |
| `double_click`       | Double-click at (x, y) |
| `scroll`             | Scroll wheel up or down |
| `type_text`          | Type a string via keyboard |
| `press_key`          | Press a key combo e.g. `ctrl+c`, `cmd+space` |
| `open_application`   | Open an app by name |
| `open_url`           | Open an `http`/`https` URL in the default browser |
| `run_command`        | Run an installed CLI executable and return stdout/stderr |
| `get_active_window_bounds` | Get the active window position and size |
| `move_active_window` | Move the active window to a screen position |
| `resize_active_window` | Resize the active window |
| `wait`               | Pause for N milliseconds |

## Project Structure

```
src/
  AIDeskAssistant/
    Program.cs                    # CLI entry point (REPL loop)
    PlatformServiceFactory.cs     # Selects the right impl at runtime
    Services/
      AIService.cs                # OpenAI chat + tool-calling loop
      IScreenshotService.cs
      IMouseService.cs
      IKeyboardService.cs
      ITerminalService.cs
      IWindowService.cs
    Platform/
      Windows/                    # Win32 P/Invoke implementations
      MacOS/                      # CoreGraphics / osascript implementations
    Tools/
      DesktopToolDefinitions.cs   # ChatTool schemas for OpenAI
      DesktopToolExecutor.cs      # Dispatches tool calls to services
    Models/
      ScreenInfo.cs
      MouseButton.cs
tests/
  AIDeskAssistant.Tests/          # xUnit tests
```

## Running Tests

```bash
dotnet test
```

## macOS Permissions

On macOS you must grant **Accessibility** and **Screen Recording** permissions to Terminal (or whichever app you run the assistant from) in **System Settings → Privacy & Security**.

## License

MIT
