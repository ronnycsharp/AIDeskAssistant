using System.Diagnostics;
using System.Text.Json;
using AIDeskAssistant.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIDeskAssistant.Mcp;

/// <summary>
/// MCP tools for text-to-speech and voice configuration.
/// Uses the OS TTS engine (macOS: <c>say</c>, Windows: PowerShell SAPI, Linux: <c>espeak</c>).
/// </summary>
internal static class SpeechMcpTools
{
    private static readonly IReadOnlyList<object> EmptyMetadata = [];

    // macOS built-in voices (subset of common ones; the full list is returned by `say -v ?`).
    private static readonly string[] MacOsVoices =
    [
        "Alex", "Daniel", "Fred", "Karen", "Moira", "Rishi", "Samantha",
        "Tessa", "Thomas", "Veena", "Victoria",
    ];

    private static readonly string[] WindowsVoices =
    [
        "Microsoft David", "Microsoft Zira", "Microsoft Mark",
    ];

    // The preferred voice is stored in an in-memory field; persistence is via RealtimeVoicePreferenceStore.
    private static string _voice = RealtimeVoicePreferenceStore.TryLoadVoice() ?? DefaultVoice();

    public static IEnumerable<McpServerTool> CreateAll() =>
    [
        new SpeakTextTool(),
        new GetVoicesTool(),
        new SetVoiceTool(),
    ];

    // ── speak_text ────────────────────────────────────────────────────────────

    private sealed class SpeakTextTool : McpServerTool
    {
        private static readonly Tool Proto = new()
        {
            Name = "speak_text",
            Description = "Converts text to speech and plays it on the local machine using the OS TTS engine. On macOS this uses the 'say' command; on Windows it uses PowerShell's System.Speech; on Linux it uses espeak/spd-say.",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "text": {
                  "type": "string",
                  "description": "The text to speak aloud."
                },
                "voice": {
                  "type": "string",
                  "description": "Optional voice name to use for this utterance. If omitted the currently configured voice is used."
                }
              },
              "required": ["text"]
            }
            """),
        };

        public override Tool ProtocolTool => Proto;
        public override IReadOnlyList<object> Metadata => EmptyMetadata;

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string? text = GetStringArg(request.Params?.Arguments, "text");
            if (string.IsNullOrWhiteSpace(text))
                return MakeError("Parameter 'text' is required.");

            string? voiceOverride = GetStringArg(request.Params?.Arguments, "voice");
            string voice = voiceOverride ?? _voice;

            try
            {
                await SpeakAsync(text, voice, cancellationToken);
                return MakeOk($"Spoken: \"{Truncate(text, 80)}\" (voice: {voice}).");
            }
            catch (Exception ex)
            {
                return MakeError($"TTS failed: {ex.Message}");
            }
        }
    }

    // ── get_voices ────────────────────────────────────────────────────────────

    private sealed class GetVoicesTool : McpServerTool
    {
        private static readonly Tool Proto = new()
        {
            Name = "get_voices",
            Description = "Returns the list of available TTS voices for the current operating system and the currently selected voice.",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""),
        };

        public override Tool ProtocolTool => Proto;
        public override IReadOnlyList<object> Metadata => EmptyMetadata;

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string current = _voice;
            string[] voices = GetAvailableVoices();
            string list = string.Join(", ", voices);
            return ValueTask.FromResult(MakeOk($"Current voice: {current}\nAvailable voices: {list}"));
        }
    }

    // ── set_voice ─────────────────────────────────────────────────────────────

    private sealed class SetVoiceTool : McpServerTool
    {
        private static readonly Tool Proto = new()
        {
            Name = "set_voice",
            Description = "Sets the TTS voice to use for speak_text. The change is persisted and survives restarts.",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "voice": {
                  "type": "string",
                  "description": "Voice name, e.g. 'Samantha' on macOS or 'Microsoft David' on Windows."
                }
              },
              "required": ["voice"]
            }
            """),
        };

        public override Tool ProtocolTool => Proto;
        public override IReadOnlyList<object> Metadata => EmptyMetadata;

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string? voice = GetStringArg(request.Params?.Arguments, "voice");
            if (string.IsNullOrWhiteSpace(voice))
                return ValueTask.FromResult(MakeError("Parameter 'voice' is required."));

            _voice = voice.Trim();
            RealtimeVoicePreferenceStore.SaveVoice(_voice);
            return ValueTask.FromResult(MakeOk($"Voice set to '{_voice}'."));
        }
    }

    // ── Platform TTS ──────────────────────────────────────────────────────────

    private static async Task SpeakAsync(string text, string voice, CancellationToken ct)
    {
        (string fileName, string arguments) = BuildTtsCommand(text, voice);

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start TTS process '{fileName}'.");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            string err = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"TTS process exited with code {process.ExitCode}. {err}".Trim());
        }
    }

    private static (string fileName, string arguments) BuildTtsCommand(string text, string voice)
    {
        // Escape single-quotes in the text for shell safety.
        string escaped = text.Replace("'", "'\\''");

        if (OperatingSystem.IsMacOS())
        {
            string args = string.IsNullOrWhiteSpace(voice)
                ? $"'{escaped}'"
                : $"-v '{voice}' '{escaped}'";
            return ("say", args);
        }

        if (OperatingSystem.IsWindows())
        {
            // Use PowerShell System.Speech to speak the text.
            string psEscaped = text.Replace("'", "''");
            string psVoice = string.IsNullOrWhiteSpace(voice) ? string.Empty
                : $"$tts.SelectVoice('{voice.Replace("'", "''")}');";
            string script =
                $"Add-Type -AssemblyName System.Speech;" +
                $"$tts = New-Object System.Speech.Synthesis.SpeechSynthesizer;" +
                $"{psVoice}" +
                $"$tts.Speak('{psEscaped}');";
            return ("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");
        }

        // Linux / other: try espeak, fall back to spd-say.
        return (FindLinuxTtsBinary(), $"'{escaped}'");
    }

    private static string FindLinuxTtsBinary()
    {
        foreach (string bin in new[] { "espeak-ng", "espeak", "spd-say" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("which", bin)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                });
                p?.WaitForExit();
                if (p?.ExitCode == 0)
                    return bin;
            }
            catch { /* continue */ }
        }
        return "espeak";
    }

    private static string[] GetAvailableVoices()
    {
        if (OperatingSystem.IsMacOS()) return MacOsVoices;
        if (OperatingSystem.IsWindows()) return WindowsVoices;
        return ["espeak"];
    }

    private static string DefaultVoice()
    {
        if (OperatingSystem.IsMacOS()) return "Samantha";
        if (OperatingSystem.IsWindows()) return "Microsoft David";
        return "espeak";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetStringArg(IDictionary<string, JsonElement>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out JsonElement el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static CallToolResult MakeOk(string text) =>
        new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
        };

    private static CallToolResult MakeError(string text) =>
        new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = true,
        };
}
