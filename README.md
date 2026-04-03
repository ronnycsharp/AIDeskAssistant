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

This project is licensed under the MIT License. See the `LICENSE` file.
