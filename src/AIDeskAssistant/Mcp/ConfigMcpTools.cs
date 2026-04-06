using System.Text.Json;
using AIDeskAssistant.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIDeskAssistant.Mcp;

/// <summary>MCP tools for runtime configuration (language, api-key info, current settings).</summary>
internal static class ConfigMcpTools
{
    private static readonly IReadOnlyList<object> EmptyMetadata = [];

    public static IEnumerable<McpServerTool> CreateAll() =>
    [
        new SetLanguageTool(),
        new GetLanguageTool(),
        new GetConfigTool(),
    ];

    // ── set_language ─────────────────────────────────────────────────────────

    private sealed class SetLanguageTool : McpServerTool
    {
        private static readonly Tool Proto = new()
        {
            Name = "set_language",
            Description = "Sets the interaction language for the AI assistant and speech output. Use 'de' for German or 'en' for English. The setting is persisted and also applies to TTS (speak_text).",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "language": {
                  "type": "string",
                  "enum": ["de", "en"],
                  "description": "Language code: 'de' for German, 'en' for English."
                }
              },
              "required": ["language"]
            }
            """),
        };

        public override Tool ProtocolTool => Proto;
        public override IReadOnlyList<object> Metadata => EmptyMetadata;

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string? lang = GetStringArg(request.Params?.Arguments, "language");
            if (string.IsNullOrWhiteSpace(lang))
                return Error("Parameter 'language' is required. Use 'de' or 'en'.");

            string set = LanguagePreferenceStore.Set(lang);
            string display = LanguagePreferenceStore.CurrentDisplayName;
            return Ok($"Language set to {display} ({set}).");
        }
    }

    // ── get_language ─────────────────────────────────────────────────────────

    private sealed class GetLanguageTool : McpServerTool
    {
        private static readonly Tool Proto = new()
        {
            Name = "get_language",
            Description = "Returns the currently configured interaction language ('de' or 'en').",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""),
        };

        public override Tool ProtocolTool => Proto;
        public override IReadOnlyList<object> Metadata => EmptyMetadata;

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string code = LanguagePreferenceStore.Current;
            string display = LanguagePreferenceStore.CurrentDisplayName;
            return Ok($"Current language: {display} ({code}).");
        }
    }

    // ── get_config ────────────────────────────────────────────────────────────

    private sealed class GetConfigTool : McpServerTool
    {
        private static readonly Tool Proto = new()
        {
            Name = "get_config",
            Description = "Returns the current AIDeskAssistant configuration: language, OpenAI model, max tool rounds, and whether an API key is set.",
            InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}"""),
        };

        public override Tool ProtocolTool => Proto;
        public override IReadOnlyList<object> Metadata => EmptyMetadata;

        public override ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            string lang = $"{LanguagePreferenceStore.CurrentDisplayName} ({LanguagePreferenceStore.Current})";
            string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
            string realtimeModel = Environment.GetEnvironmentVariable("OPENAI_REALTIME_MODEL") ?? "gpt-realtime";
            bool hasApiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
            int maxRounds = TryGetPositiveInt(Environment.GetEnvironmentVariable("AIDESK_MAX_TOOL_ROUNDS"), 60);

            return Ok($"""
                Language:         {lang}
                OpenAI model:     {model}
                Realtime model:   {realtimeModel}
                API key set:      {(hasApiKey ? "yes" : "no")}
                Max tool rounds:  {maxRounds}
                """);
        }

        private static int TryGetPositiveInt(string? value, int fallback)
            => int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetStringArg(IDictionary<string, JsonElement>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out JsonElement el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static ValueTask<CallToolResult> Ok(string text) =>
        ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
        });

    private static ValueTask<CallToolResult> Error(string text) =>
        ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = true,
        });
}
