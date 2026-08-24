using System.Runtime.CompilerServices;
using ALCops.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Services;

internal static class McpHost
{
    // NoInlining ensures this method is JIT-compiled separately from the caller, so the assembly
    // resolver registered by BcToolsLocator.ResolveAndRegister is in place before any BC types
    // (referenced by ProjectLoader, CodeFixRunner, etc.) are loaded. Still required even though the
    // resolution chain shrank: the BC DLLs are not in our output directory.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task RunAsync(string[] args, BcToolsLocator toolsLocator, ProxyOptions proxyOptions)
    {
        // MCP servers must use stdio for protocol communication.
        // All diagnostic output goes to stderr so it doesn't interfere with the JSON-RPC channel.

        var builder = Host.CreateApplicationBuilder(args);

        // Suppress all stdout logging — MCP protocol uses stdout
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Register ALCops services
        builder.Services.AddSingleton(toolsLocator);
        builder.Services.AddSingleton<ProjectLoader>();
        builder.Services.AddSingleton<ProjectSessionManager>();
        builder.Services.AddSingleton<CodeFixRunner>();
        builder.Services.AddSingleton<ExternalAnalyzerLoader>();
        builder.Services.AddSingleton<RulesetLoader>();
        builder.Services.AddSingleton<ProjectAnalyzerResolver>();

        // Registered even with --no-proxy: list_rules falls back to the discovered project too.
        builder.Services.AddSingleton(sp =>
            new WorkspaceStartupResolver(
                sp.GetRequiredService<ProjectAnalyzerResolver>(),
                sp.GetRequiredService<ExternalAnalyzerLoader>(),
                sp.GetRequiredService<ILogger<WorkspaceStartupResolver>>(),
                proxyOptions.Projects));

        // Register almcp proxy (optional — gracefully unavailable if almcp not found)
        if (!proxyOptions.ProxyDisabled)
        {
            builder.Services.AddSingleton(sp =>
                new AlMcpProxy(
                    sp.GetRequiredService<BcToolsLocator>(),
                    sp.GetRequiredService<WorkspaceStartupResolver>(),
                    sp.GetRequiredService<ILogger<AlMcpProxy>>(),
                    proxyOptions.PassthroughArgs));
            builder.Services.AddHostedService<AlMcpProxyStartup>();
        }

        // Register MCP server with stdio transport and auto-discover tools
        var mcpBuilder = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "alcops",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString() ?? "0.1.0"
                };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        // Dynamic handlers: proxy MS tools alongside our native tools
        if (!proxyOptions.ProxyDisabled)
        {
            mcpBuilder
                .WithListToolsHandler(async (request, ct) =>
                {
                    var proxy = request.Services?.GetService<AlMcpProxy>();
                    if (proxy is null || !proxy.IsStarted)
                        return new ListToolsResult();

                    var tools = proxy.GetCachedTools();
                    return new ListToolsResult
                    {
                        Tools = tools.Select(t => t.ProtocolTool).ToList()
                    };
                })
                .WithCallToolHandler(async (request, ct) =>
                {
                    var proxy = request.Services!.GetRequiredService<AlMcpProxy>();
                    return await proxy.ForwardAsync(
                        request.Params!.Name,
                        request.Params.Arguments,
                        ct);
                });
        }

        await builder.Build().RunAsync();
    }
}
