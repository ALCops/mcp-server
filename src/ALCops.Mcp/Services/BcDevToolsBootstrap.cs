using System.Runtime.Loader;

namespace ALCops.Mcp.Services;

internal static class BcDevToolsBootstrap
{
    private const string MarkerDll = "Microsoft.Dynamics.Nav.CodeAnalysis.dll";

    /// <summary>
    /// Resolves BC DevTools location and registers an assembly resolver.
    /// Must be called at the very top of Program.cs, before any BC types are referenced.
    /// </summary>
    /// <returns>The resolved DLL directory, or null if not found.</returns>
    public static string? ResolveAndRegister()
    {
        var path = Resolve();
        if (path is not null)
        {
            RegisterAssemblyResolver(path);
            Console.Error.WriteLine($"BC DevTools: {path}");
        }
        else
        {
            Console.Error.WriteLine("Warning: BC DevTools not found. Standard BC cops will not be available.");
            Console.Error.WriteLine("  Install the AL Language extension for VS Code, or set BCDEVELOPMENTTOOLSPATH.");
        }

        return path;
    }

    private static string? Resolve()
    {
        return TryEnvironmentVariable()
            ?? TryAlExtension()
            ?? TryNuGetChain()
            ?? TryExeRelative();
    }

    private static string? TryEnvironmentVariable()
    {
        var envPath = Environment.GetEnvironmentVariable("BCDEVELOPMENTTOOLSPATH");
        if (string.IsNullOrEmpty(envPath))
            return null;

        // Standard layout: <root>/net8.0/
        var net8 = Path.Combine(envPath, "net8.0");
        if (HasMarkerDll(net8))
            return net8;

        // Direct: DLLs in the path itself
        if (HasMarkerDll(envPath))
            return envPath;

        return null;
    }

    private static string? TryAlExtension()
    {
        var locator = new AlExtensionLocator();
        var analyzersPath = locator.GetAnalyzersPath();
        if (analyzersPath is null)
            return null;

        // Nav.CodeAnalysis.dll lives in both bin/Analyzers/ and bin/{platform}/
        if (HasMarkerDll(analyzersPath))
            return analyzersPath;

        // Also check platform-specific subdirectory: bin/linux/, bin/win32/, bin/darwin/
        var binDir = Path.GetDirectoryName(analyzersPath);
        if (binDir is not null)
        {
            var platformDir = Path.Combine(binDir, GetPlatformSubdir());
            if (HasMarkerDll(platformDir))
                return platformDir;
        }

        return null;
    }

    private static string GetPlatformSubdir()
    {
        if (OperatingSystem.IsWindows()) return "win32";
        if (OperatingSystem.IsMacOS()) return "darwin";
        return "linux";
    }

    private static string? TryNuGetChain()
    {
        // NuGetDevToolsDownloader checks: .NET global tools cache -> local cache -> NuGet download
        try
        {
            var downloader = new NuGetDevToolsDownloader();
            var path = downloader.GetToolsPathAsync().GetAwaiter().GetResult();
            return path is not null && HasMarkerDll(path) ? path : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: NuGet BC DevTools resolution failed: {ex.Message}");
            return null;
        }
    }

    private static string? TryExeRelative()
    {
        // From bin/Release/net8.0/ -> 5 levels up to repo root -> DevTools/net8.0/
        var exeDir = AppContext.BaseDirectory;
        var devToolsNet8 = Path.GetFullPath(
            Path.Combine(exeDir, "..", "..", "..", "..", "..",
                "Microsoft.Dynamics.BusinessCentral.Development.Tools", "net8.0"));
        return HasMarkerDll(devToolsNet8) ? devToolsNet8 : null;
    }

    private static void RegisterAssemblyResolver(string devToolsDir)
    {
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name is null)
                return null;

            var candidate = Path.Combine(devToolsDir, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }

    private static bool HasMarkerDll(string directory)
        => Directory.Exists(directory) && File.Exists(Path.Combine(directory, MarkerDll));
}
