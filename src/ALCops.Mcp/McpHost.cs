using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ALCops.Mcp.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ALCops.Mcp.Services;

internal static class McpHost
{
    // Long enough for a normal project load, so hosts that list tools only once still get the al_*
    // tools; short enough that a broken almcp never makes the server look hung.
    private static readonly TimeSpan ListToolsReadyBudget = TimeSpan.FromSeconds(10);

    // Strips the JSON-schema "default" keyword from every node of a native tool's input schema.
    // Microsoft.Extensions.AI emits it for every parameter with a C# default, null included
    // ("default": null on every optional string). Not MoveDefaultKeywordToDescription: that appends
    // " (Default value: null)" to those parameters and duplicates the defaults our parameter
    // descriptions already state (a contract test keeps those statements in place).
    internal static readonly AIJsonSchemaCreateOptions NativeToolSchemaOptions = new()
    {
        TransformOptions = new AIJsonSchemaTransformOptions
        {
            TransformSchemaNode = (_, node) =>
            {
                if (node is JsonObject obj)
                    obj.Remove("default");
                return node;
            }
        }
    };

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

        ConfigureServices(builder.Services, toolsLocator, proxyOptions)
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
    }

    // Everything the server registers except logging and the transport, so tests can build the same
    // container and inspect the tools exactly as tools/list publishes them. Only ever called from
    // RunAsync (after the BC assembly resolver is registered) or from tests, where the BC DLLs sit in
    // the output folder. Hosted services are registered here but only start when a host runs.
    internal static IMcpServerBuilder ConfigureServices(IServiceCollection services, BcToolsLocator toolsLocator, ProxyOptions proxyOptions)
    {
        // Register ALCops services
        services.AddSingleton(toolsLocator);
        services.AddSingleton<ProjectLoader>();
        services.AddSingleton<ProjectSessionManager>();
        services.AddSingleton<CodeFixRunner>();
        services.AddSingleton<GuardedFileWriter>();
        services.AddSingleton(sp =>
        {
            var provisioner = sp.GetRequiredService<AlcopsAnalyzerProvisioner>();
            Func<string?> provisionedFolder = () =>
                provisioner.Ready is { IsCompletedSuccessfully: true, Result: not null }
                    ? provisioner.Ready.Result
                    : null;
            return new ExternalAnalyzerLoader(
                sp.GetRequiredService<BcToolsLocator>(),
                provisionedFolder);
        });
        services.AddSingleton<RulesetLoader>();
        services.AddSingleton(sp =>
            new ProjectAnalyzerResolver(
                sp.GetRequiredService<ExternalAnalyzerLoader>(),
                sp.GetRequiredService<RulesetLoader>(),
                sp.GetRequiredService<AlcopsAnalyzerProvisioner>()));

        // ALCops analyzer provisioner: uses the newest cached ALCops analyzers immediately and
        // refreshes from NuGet in the background, matched to the installed DevTools TFM.
        // Runs under --no-proxy too — the native fix tools need them.
        var analyzersOption = AlcopsAnalyzersOption.Parse(
            proxyOptions.AlcopsAnalyzers
            ?? Environment.GetEnvironmentVariable("ALCOPS_ANALYZERS"));
        services.AddSingleton(sp =>
            new AlcopsAnalyzerProvisioner(
                sp.GetRequiredService<BcToolsLocator>(),
                analyzersOption,
                null,
                null,
                sp.GetRequiredService<ILogger<AlcopsAnalyzerProvisioner>>()));
        services.AddHostedService<AlcopsAnalyzerProvisionerStartup>();

        // Registered even with --no-proxy: list_rules falls back to the discovered project too.
        services.AddSingleton(sp =>
            new WorkspaceStartupResolver(
                sp.GetRequiredService<ProjectAnalyzerResolver>(),
                sp.GetRequiredService<ExternalAnalyzerLoader>(),
                sp.GetRequiredService<AlcopsAnalyzerProvisioner>(),
                sp.GetRequiredService<ILogger<WorkspaceStartupResolver>>(),
                proxyOptions.Projects));

        // Register almcp proxy (optional — gracefully unavailable if almcp not found)
        if (!proxyOptions.ProxyDisabled)
        {
            services.AddSingleton(sp =>
                new AlMcpProxy(
                    sp.GetRequiredService<BcToolsLocator>(),
                    sp.GetRequiredService<WorkspaceStartupResolver>(),
                    sp.GetRequiredService<ILogger<AlMcpProxy>>(),
                    proxyOptions.PassthroughArgs));
            services.AddHostedService<AlMcpProxyStartup>();
        }

        // Register MCP server
        var mcpBuilder = services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "alcops",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString() ?? "0.1.0"
                };
            });

        // Native tools. Registered by hand instead of WithToolsFromAssembly(), which offers no way to
        // pass SchemaCreateOptions. Services = sp is what keeps DI parameters out of the schema, exactly
        // as the SDK's own registration does.
        foreach (var method in NativeToolMethods())
        {
            services.AddSingleton(sp => McpServerTool.Create(method, target: null,
                new McpServerToolCreateOptions { Services = sp, SchemaCreateOptions = NativeToolSchemaOptions }));
        }

        // Dynamic handlers: proxy MS tools alongside our native tools
        if (!proxyOptions.ProxyDisabled)
        {
            mcpBuilder
                .WithListToolsHandler(async (request, ct) =>
                {
                    var proxy = request.Services?.GetService<AlMcpProxy>();
                    if (proxy is null || !proxy.IsAvailable)
                        return new ListToolsResult();

                    bool ready;
                    try
                    {
                        ready = await proxy.Ready.WaitAsync(ListToolsReadyBudget, ct);
                    }
                    catch (TimeoutException)
                    {
                        // Answer with what we have and tell this session to ask again once almcp is up.
                        proxy.NotifyToolListChangedWhenReady(request.Server);
                        return new ListToolsResult();
                    }

                    if (!ready)
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

        return mcpBuilder;
    }

    // Every tool is a static method on an [McpServerToolType] class in this assembly.
    private static IEnumerable<MethodInfo> NativeToolMethods() =>
        typeof(McpHost).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
