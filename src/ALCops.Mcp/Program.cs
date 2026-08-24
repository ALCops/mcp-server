using ALCops.Mcp.Services;

// Parse our own CLI args (consumed here, not passed to the host builder).
var options = ProxyOptions.Parse(args);

// Resolve BC DevTools before anything else — this registers the assembly resolver that every BC
// type depends on. The DLLs are deliberately not in our output directory, so nothing referencing
// Nav.CodeAnalysis may be JIT'd before this line.
BcToolsLocator toolsLocator;
try
{
    toolsLocator = BcToolsLocator.ResolveAndRegister(options.DevToolsPath);
}
catch (InvalidOperationException ex)
{
    // Nothing works without the toolchain, and a stack trace would only bury the fix.
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// Host setup is in a separate method to ensure the assembly resolver is registered
// before JIT compilation encounters types that reference Nav.CodeAnalysis.
await McpHost.RunAsync(args, toolsLocator, options);
return 0;
