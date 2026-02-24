using System.Runtime.CompilerServices;
using ALCops.Mcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Services;

internal static class McpHost
{
    // NoInlining ensures this method is JIT-compiled separately from the caller,
    // so the assembly resolver registered in BcDevToolsBootstrap is available before
    // any BC types (referenced by AnalyzerRegistry, ProjectLoader, etc.) are loaded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task RunAsync(string[] args, string? bcDevToolsDir)
    {
        // MCP servers must use stdio for protocol communication.
        // All diagnostic output goes to stderr so it doesn't interfere with the JSON-RPC channel.

        var builder = Host.CreateApplicationBuilder(args);

        // Suppress all stdout logging — MCP protocol uses stdout
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Register ALCops services
        builder.Services.AddSingleton<AlExtensionLocator>();
        builder.Services.AddSingleton<NuGetDevToolsDownloader>();
        builder.Services.AddSingleton(new DevToolsLocator(bcDevToolsDir));
        builder.Services.AddSingleton<AnalyzerRegistry>();
        builder.Services.AddSingleton<ProjectLoader>();
        builder.Services.AddSingleton<ProjectSessionManager>();
        builder.Services.AddSingleton<DiagnosticsRunner>();
        builder.Services.AddSingleton<CodeFixRunner>();
        builder.Services.AddSingleton<ExternalAnalyzerLoader>();
        builder.Services.AddSingleton<RulesetLoader>();
        builder.Services.AddSingleton<ProjectAnalyzerResolver>();

        // Register MCP server with stdio transport and auto-discover tools
        builder.Services
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

        await builder.Build().RunAsync();
    }
}
