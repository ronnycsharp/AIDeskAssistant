using AIDeskAssistant.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AIDeskAssistant.Mcp;

/// <summary>
/// Runs AIDeskAssistant as an MCP server over stdio so that MCP clients such as
/// VS Code Copilot, Claude Desktop, or any other MCP-compatible tool can connect
/// and use all desktop-control and speech tools directly.
/// </summary>
internal static class McpServerRunner
{
    /// <summary>
    /// Builds and starts the MCP stdio server.
    /// All desktop tools and speech/configuration tools are registered.
    /// Diagnostics are written to stderr so stdout stays clean for the MCP protocol.
    /// </summary>
    public static async Task<int> RunAsync(
        DesktopToolExecutor executor,
        string[] args,
        CancellationToken ct = default)
    {
        // Build all MCP tools from the existing desktop tool definitions.
        List<McpServerTool> tools =
        [
            .. DesktopToolDefinitions.FunctionDefinitions
                .Select(def => (McpServerTool)new DesktopMcpTool(def, executor)),
            .. SpeechMcpTools.CreateAll(),
            .. ConfigMcpTools.CreateAll(),
        ];

        var builder = Host.CreateApplicationBuilder(args);

        // Suppress all Generic Host logging to keep stdout clean for the MCP protocol.
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter(_ => false);

        builder.Services
            .AddMcpServer(opt =>
            {
                opt.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "AIDeskAssistant",
                    Version = "1.0.0",
                };
            })
            .WithStdioServerTransport()
            .WithTools(tools);

        IHost host = builder.Build();
        await host.RunAsync(ct);
        return 0;
    }
}
