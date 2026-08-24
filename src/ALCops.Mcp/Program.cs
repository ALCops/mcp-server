using ALCops.Mcp.Services;

// Resolve BC DevTools before anything else — registers assembly resolver
// so that AnalyzerRegistry can load ALCops cops that reference Nav.CodeAnalysis.
var bcDevToolsDir = BcDevToolsBootstrap.ResolveAndRegister();

// Parse proxy-related CLI args (consumed here, not passed to the host builder)
var proxyOptions = ParseProxyOptions(args);

// Host setup is in a separate method to ensure the assembly resolver is registered
// before JIT compilation encounters types that reference Nav.CodeAnalysis.
await McpHost.RunAsync(args, bcDevToolsDir, proxyOptions);

static ProxyOptions ParseProxyOptions(string[] args)
{
    bool disabled = false;
    string? almcpPath = null;
    var passthroughArgs = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--no-proxy":
                disabled = true;
                break;
            case "--almcp-path" when i + 1 < args.Length:
                almcpPath = args[++i];
                break;
            // Pass through args that almcp also understands
            case "--enablecodeanalysis" or "--codeanalyzers" or "--rulesetpath"
                or "--settingspath" or "--noauth" or "--enableexternalrulesets"
                or "--locale" or "--nolog" when i + 1 < args.Length:
                passthroughArgs.Add(args[i]);
                passthroughArgs.Add(args[++i]);
                break;
        }
    }

    return new ProxyOptions(disabled, almcpPath, passthroughArgs.Count > 0 ? passthroughArgs.ToArray() : null);
}
