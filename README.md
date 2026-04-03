# AIDeskAssistant

`AIDeskAssistant` is a work-in-progress desktop automation assistant built with C# (.NET 8) and the OpenAI API.

The goal is simple: control the desktop with natural language. The assistant can inspect the screen, open apps, click, type, move windows, run terminal commands, and work through longer tasks step by step.

The repository currently contains both macOS and Windows implementations, but the project has only been tested on macOS so far.

## Current Status

- Work in progress.
- Primary target: desktop control through natural-language commands.
- Tested so far on macOS only.
- Windows is currently untested and may be supported later.
- Includes a macOS status bar app called `AIDesk`.
- `AIDesk` supports text input, live microphone input, and spoken assistant responses.

## What AIDesk Can Do

- Understand natural-language desktop tasks.
- Take screenshots so the model can reason about the current UI.
- Move the mouse, click, scroll, and type.
- Open and focus desktop applications.
- Control the active window.
- Run terminal commands and return the result.
- Use a menu bar workflow on macOS for faster voice-driven interaction.

## AIDesk Status Bar Mode

On macOS, the project can run as a status bar tool named `AIDesk`.

That mode is intended for quick desktop control without staying inside the terminal:

- text input directly from the status bar popover
- live speech input from the microphone
- streamed spoken output from the assistant
- local host process for low-latency tool execution and audio streaming

This is currently the main interaction model for hands-free usage.

## Example Commands

The assistant is intended for commands like these:

```text
Open Excel and write the numbers 1 to 10 into a column.
Continue the Excel sheet to 20 and save it on the desktop as Exceltest AI.
Open Word and write a short project update.
Open TextEdit and insert a short poem.
Open Safari and go to https://example.com.
Move the active window to x 50 y 50 and resize it to 1280 by 800.
Run git status and summarize the changes.
Take a screenshot and tell me what is visible.
```

## Features

- LLM-driven desktop automation with tool calling
- Screen inspection through screenshots
- Mouse and keyboard control
- Application launching and focusing
- Browser URL opening
- Terminal command execution
- Active window inspection, movement, and resizing
- CLI mode for direct prompting
- macOS menu bar mode with speech input and speech output
- Configurable multi-step tool loop for longer tasks

## Requirements

| Platform | Runtime | Status |
|----------|---------|--------|
| macOS | .NET 8+ | tested |
| Windows | .NET 8+ | currently untested, possible later support |

## Getting Started

### 1. Clone and build

```bash
git clone https://github.com/ronnycsharp/AIDeskAssistant.git
cd AIDeskAssistant
dotnet build AIDeskAssistant.sln
```

### 2. Configure your API key

You can provide the OpenAI API key either through environment variables or through a local `.env` file.

macOS or Linux:

```bash
export OPENAI_API_KEY="sk-..."
```

Windows PowerShell:

```powershell
$env:OPENAI_API_KEY = "sk-..."
```

Or create a local `.env` file in the repository root:

```dotenv
OPENAI_API_KEY=sk-...
OPENAI_MODEL=gpt-5-mini
AIDESK_MAX_TOOL_ROUNDS=120
```

The local `.env` file is ignored by git.

### 3. Run the CLI

```bash
dotnet run --project src/AIDeskAssistant
```

### 4. Run the macOS status bar tool

```bash
dotnet run --project src/AIDeskAssistant -- --menu-bar
```

Useful companion commands:

```bash
dotnet run --project src/AIDeskAssistant -- --menu-bar-status
dotnet run --project src/AIDeskAssistant -- --menu-bar-stop
```

### 5. Optional debug logging

```bash
dotnet run --project src/AIDeskAssistant -- --debug-model-io
```

This creates a local session folder under `.aidesk-debug/` with tool traces, screenshots, and assistant responses for debugging runs.

## CLI Commands

| Command | Description |
|---------|-------------|
| `/help` | Show help text |
| `/clear`, `/reset` | Clear conversation history |
| `/quit`, `/exit` | Exit the program |
| `Ctrl+C` | Cancel the current AI request |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `OPENAI_API_KEY` | required | OpenAI API key |
| `OPENAI_MODEL` | `gpt-5-mini` | Chat model used by CLI mode |
| `OPENAI_REALTIME_MODEL` | `gpt-realtime` | Realtime model used by macOS menu bar mode |
| `AIDESK_MAX_TOOL_ROUNDS` | `60` | Maximum tool rounds before the assistant stops |
| `AIDESK_DEBUG_MODEL_IO` | unset | Enable local debug logging for model input, screenshots, and tool traces |
| `AIDESK_DEBUG_DIR` | `.aidesk-debug` | Base directory for debug sessions |
| `AIDESK_MENU_BAR_STATUS_FILE` | temp dir | Tracks the running menu bar host |
| `AIDESK_SCREENSHOT_MAX_DIMENSION` | `1280` | Maximum image dimension for screenshots sent to the model |
| `AIDESK_SCREENSHOT_JPEG_QUALITY` | `60` | JPEG quality for optimized screenshots |
| `AIDESK_REALTIME_VOICE` | `alloy` | Voice used for spoken output |
| `AIDESK_REALTIME_SAMPLE_RATE` | `24000` | Sample rate for menu bar voice recording |
| `AIDESK_MENU_BAR_SELF_TEST_TEXT` | unset | Optional automatic self-test prompt for the menu bar helper |
| `AIDESK_MENU_BAR_LOG_FILE` | unset | Optional log file for Swift menu bar diagnostics |
| `AIDESK_MENU_BAR_SILENCE_COMMIT_SECONDS` | `0.9` | Silence duration before auto-commit of live microphone input |
| `AIDESK_MENU_BAR_SILENCE_THRESHOLD` | `0.015` | Peak threshold for speech detection |
| `AIDESK_MENU_BAR_RMS_THRESHOLD` | `0.003` | Minimum RMS threshold for speech detection |
| `AIDESK_MENU_BAR_MIN_SPEECH_SECONDS` | `0.25` | Minimum speech duration before silence can auto-commit |
| `AIDESK_MENU_BAR_RMS_CALIBRATION_SECONDS` | `0.35` | Initial calibration period for background RMS |
| `AIDESK_MENU_BAR_NOISE_FLOOR_MULTIPLIER` | `2.5` | Adaptive multiplier for speech threshold calculation |
| `AIDESK_MENU_BAR_NOISE_FLOOR_PADDING` | `0.002` | Additional safety margin for adaptive speech threshold |

## Available Desktop Tools

The model can call tools such as:

- `take_screenshot`
- `get_screen_info`
- `get_cursor_position`
- `move_mouse`
- `click`
- `double_click`
- `scroll`
- `type_text`
- `press_key`
- `open_application`
- `focus_application`
- `open_url`
- `run_command`
- `click_dock_application`
- `click_apple_menu_item`
- `click_system_settings_sidebar_item`
- `focus_frontmost_window_content`
- `get_active_window_bounds`
- `move_active_window`
- `resize_active_window`
- `wait`

## Project Structure

```text
src/
  AIDeskAssistant/
    Program.cs
    PlatformServiceFactory.cs
    Services/
    Platform/
    Tools/
    Models/
tests/
  AIDeskAssistant.Tests/
```

## Running Tests

```bash
dotnet test AIDeskAssistant.sln
```

## macOS Permissions

On macOS you must grant the required permissions to the host application you run the assistant from.

- Accessibility
- Screen Recording
- Microphone for menu bar voice mode

You can find them in `System Settings -> Privacy & Security`.

## Notes for Publishing

- `.env` is ignored and should stay local.
- `.aidesk-debug/` is ignored and should stay local.
- This repository is still evolving and APIs, prompts, and desktop workflows may change.

## License

MIT

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
- 🎙️ **macOS menu bar mode** — optional status icon with text input, live microphone streaming, and low-latency spoken responses streamed from the OpenAI Realtime API
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

You can use either shell environment variables or a local `.env` file.

```bash
# Linux / macOS
export OPENAI_API_KEY="sk-..."

# Windows PowerShell
$env:OPENAI_API_KEY = "sk-..."
```

Or create a `.env` file in the repository root:

```dotenv
OPENAI_API_KEY=sk-...
OPENAI_MODEL=gpt-4o
AIDESK_MAX_TOOL_ROUNDS=120
```

Or you will be prompted for it on first run.

### 3. Run

```bash
dotnet run --project src/AIDeskAssistant
```

macOS menu bar mode:

```bash
dotnet run --project src/AIDeskAssistant -- --menu-bar
```

The menu bar helper uses the OpenAI Realtime API for voice output and now streams text and PCM audio deltas from the local host to the Swift status item. That removes the old wait-for-full-WAV step and noticeably reduces speech latency.

The `Aufnehmen` button now streams microphone PCM chunks to the local host while you are speaking. Pressing `Stop` commits the already-uploaded audio buffer to the active Realtime session instead of uploading a finished WAV file after the fact.

By default, the menu bar helper also auto-commits after a short silence once speech has been detected, so `Stop` is usually optional.

If `AIDESK_MENU_BAR_LOG_FILE` is set, the Swift helper now logs capture RMS/peak levels, noise-floor calibration, speech detection, and silence-triggered auto-commits.

If you probe the local menu bar HTTP endpoint with `curl`, disable audio in the response unless you explicitly want the base64 WAV payload printed to the terminal:

```bash
curl -s -X POST 'http://127.0.0.1:54502/message?includeAudio=false' \
  -H 'Content-Type: application/json' \
  -d '{"text":"Sag kurz, dass die Verbindung funktioniert."}'
```

Without `includeAudio=false`, the response can contain a large `audioBase64` field, which is inconvenient in terminals and can make the VS Code integrated terminal sluggish.

For low-latency diagnostics you can also inspect the streaming endpoints directly. They emit newline-delimited JSON with `text_delta`, `audio_delta`, and `completed` events:

```bash
curl -N -X POST 'http://127.0.0.1:54502/message-stream?includeAudio=false' \
  -H 'Content-Type: application/json' \
  -d '{"text":"Sag kurz, dass Realtime-Streaming aktiv ist."}'
```

The local menu bar host also exposes live microphone control endpoints used by the Swift helper:

- `POST /audio-live/start` opens a new live audio capture session and returns a `sessionId`
- `POST /audio-live/chunk?sessionId=...` appends raw `pcm_s16le` mono 24 kHz bytes to that session
- `POST /audio-live/commit-stream?sessionId=...` commits the buffered live audio and streams the assistant response as NDJSON
- `POST /audio-live/cancel?sessionId=...` abandons a live microphone session before commit

Check whether the background menu bar host is still running:

```bash
dotnet run --project src/AIDeskAssistant -- --menu-bar-status
```

Stop a running background menu bar host:

```bash
dotnet run --project src/AIDeskAssistant -- --menu-bar-stop
```

In VS Code you can also start it from the launch menu with `AIDeskAssistant Menu Bar`. That configuration starts the menu bar host in the background so VS Code is not held open by the long-running status icon process.

Optional:

```bash
export AIDESK_MAX_TOOL_ROUNDS=120
```

Debug model I/O for a CLI run:

```bash
dotnet run --project src/AIDeskAssistant -- --debug-model-io
```

This writes a timestamped session folder under `.aidesk-debug/` by default. It contains:

- the exact prepared user message sent to the model, including screen info
- a tool trace log with tool calls and summarized results
- the assistant's final text responses
- the actual screenshot image files attached to the model, plus a small metadata file for each screenshot

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
| `OPENAI_REALTIME_MODEL` | `gpt-realtime` | Realtime model used by macOS menu bar mode |
| `AIDESK_MAX_TOOL_ROUNDS` | `60` | Maximum agent tool rounds per task before the assistant stops and asks to continue |
| `AIDESK_DEBUG_MODEL_IO` | *(unset)* | Enable CLI debug logging for prepared model input, tool trace, and attached screenshots |
| `AIDESK_DEBUG_DIR` | `.aidesk-debug` | Base directory where timestamped debug sessions are written |
| `AIDESK_MENU_BAR_STATUS_FILE` | temp dir | Optional path used to track the running background menu bar host |
| `AIDESK_SCREENSHOT_MAX_DIMENSION` | `1280` | Maximum width or height used for screenshots sent to the model |
| `AIDESK_SCREENSHOT_JPEG_QUALITY` | `60` | JPEG quality for optimized screenshots |
| `AIDESK_REALTIME_VOICE` | `alloy` | Voice name for macOS menu bar mode |
| `AIDESK_REALTIME_SAMPLE_RATE` | `24000` | Expected PCM sample rate for menu bar voice recording |
| `AIDESK_MENU_BAR_SELF_TEST_TEXT` | *(unset)* | Optional text sent automatically by the Swift menu bar helper shortly after launch for end-to-end voice self-tests |
| `AIDESK_MENU_BAR_LOG_FILE` | *(unset)* | Optional file path for Swift menu bar diagnostics, including stream, playback, and cancel events |
| `AIDESK_MENU_BAR_SILENCE_COMMIT_SECONDS` | `0.9` | Silence duration after detected speech before the Swift helper auto-commits live microphone input |
| `AIDESK_MENU_BAR_SILENCE_THRESHOLD` | `0.015` | Normalized peak amplitude threshold used by the Swift helper to decide whether a live mic chunk contains speech |
| `AIDESK_MENU_BAR_RMS_THRESHOLD` | `0.003` | Minimum normalized RMS threshold used alongside peak detection for speech detection |
| `AIDESK_MENU_BAR_MIN_SPEECH_SECONDS` | `0.25` | Minimum elapsed speaking time before silence can trigger an automatic live mic commit |
| `AIDESK_MENU_BAR_RMS_CALIBRATION_SECONDS` | `0.35` | Initial window used to estimate microphone/background RMS before speech detection adapts the threshold |
| `AIDESK_MENU_BAR_NOISE_FLOOR_MULTIPLIER` | `2.5` | Multiplier applied to the calibrated background RMS to derive the adaptive speech threshold |
| `AIDESK_MENU_BAR_NOISE_FLOOR_PADDING` | `0.002` | Small additive safety margin applied on top of the adaptive RMS threshold |

The app automatically loads `.env` from the repository root or the project folder if present. Existing shell environment variables take precedence.

Screenshots sent to the model are automatically resized and JPEG-compressed when that reduces payload size; otherwise the original PNG is kept.

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
| `focus_application`  | Bring an already running app to the foreground |
| `open_url`           | Open an `http`/`https` URL in the default browser |
| `run_command`        | Run an installed CLI executable and return stdout/stderr |
| `click_dock_application` | Click a Dock app icon through macOS Accessibility |
| `click_apple_menu_item` | Click an Apple menu item through macOS Accessibility |
| `click_system_settings_sidebar_item` | Click a System Settings sidebar item through macOS Accessibility |
| `focus_frontmost_window_content` | Focus a likely document/content area inside the frontmost macOS window |
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

For menu bar mode, the Swift helper also needs **Microphone** access.

## License

MIT
