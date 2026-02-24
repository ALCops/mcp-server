using ALCops.Mcp.Services;

// Resolve BC DevTools before anything else — registers assembly resolver
// so that AnalyzerRegistry can load ALCops cops that reference Nav.CodeAnalysis.
var bcDevToolsDir = BcDevToolsBootstrap.ResolveAndRegister();

// Host setup is in a separate method to ensure the assembly resolver is registered
// before JIT compilation encounters types that reference Nav.CodeAnalysis.
await McpHost.RunAsync(args, bcDevToolsDir);
