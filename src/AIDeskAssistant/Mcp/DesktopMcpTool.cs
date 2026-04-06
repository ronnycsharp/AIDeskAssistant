using System.Text.Json;
using AIDeskAssistant.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIDeskAssistant.Mcp;

/// <summary>
/// Exposes a single <see cref="DesktopToolExecutor"/> tool as an MCP <see cref="McpServerTool"/>.
/// Each instance wraps one entry from <see cref="DesktopToolDefinitions.FunctionDefinitions"/>.
/// </summary>
internal sealed class DesktopMcpTool : McpServerTool
{
    private static readonly IReadOnlyList<object> EmptyMetadata = [];
    private static readonly JsonElement EmptyObjectSchema =
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");

    private readonly Tool _protocolTool;
    private readonly DesktopToolExecutor _executor;
    private readonly string _toolName;

    public override Tool ProtocolTool => _protocolTool;
    public override IReadOnlyList<object> Metadata => EmptyMetadata;

    public DesktopMcpTool(DesktopFunctionToolDefinition def, DesktopToolExecutor executor)
    {
        _toolName = def.Name;
        _executor = executor;

        JsonElement inputSchema = def.Parameters is not null
            ? JsonSerializer.Deserialize<JsonElement>(def.Parameters.ToMemory().Span)
            : EmptyObjectSchema;

        _protocolTool = new Tool
        {
            Name = def.Name,
            Description = def.Description,
            InputSchema = inputSchema,
        };
    }

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        IDictionary<string, JsonElement>? args = request.Params?.Arguments;
        string argsJson = args is { Count: > 0 }
            ? JsonSerializer.Serialize(args)
            : "{}";

        string result = _executor.Execute(_toolName, argsJson);
        bool isError = DesktopToolExecutor.IsErrorResult(result);

        return ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = result }],
            IsError = isError ? true : null,
        });
    }
}
