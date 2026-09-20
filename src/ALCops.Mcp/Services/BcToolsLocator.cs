using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ALCops.Mcp.Services;

/// <summary>
/// Locates the single directory that holds both the BC DevTools assemblies
/// (<c>Microsoft.Dynamics.Nav.*.dll</c>) and Microsoft's <c>almcp</c>. Probe order:
/// <c>--devtools-path</c> → <c>BCDEVELOPMENTTOOLSPATH</c> → dotnet tool store → hard error.
/// The AL VS Code extension is no longer probed; point <c>--devtools-path</c> at its
/// <c>bin/&lt;platform&gt;</c> folder if needed.
/// </summary>
public sealed class BcToolsLocator
{
    private const string MarkerDll = "Microsoft.Dynamics.Nav.CodeAnalysis.dll";
    private const string PackageId = "microsoft.dynamics.businesscentral.development.tools";

    // Probed in order; net10.0 first so an SDK shipping both is used at our own runtime version.
    private static readonly string[] TfmSubfolders = ["net10.0", "net8.0"];

    /// <summary>The directory holding the BC DevTools DLLs and <c>almcp</c>.</summary>
    public string ToolsDirectory { get; }

    /// <summary>Full path to whichever almcp artifact was chosen (the native launcher or the DLL).</summary>
    public string AlMcpPath { get; }

    /// <summary>
    /// Where <c>${CodeCop}</c> / <c>${analyzerFolder}</c> specs resolve to: the <c>Analyzers/</c>
    /// subfolder when present, otherwise the flat tools directory. ALCops' own analyzers come from the
    /// provisioner (<see cref="AlcopsAnalyzerProvisioner"/>); this folder serves Microsoft cops and
    /// manual layouts only.
    /// </summary>
    public string AnalyzerFolder { get; }

    /// <summary>How to launch the child <c>almcp</c> process, or <c>null</c> on 16.2-and-earlier toolchains.</summary>
    public AlMcpLaunch? AlMcp { get; }

    public bool HasAlMcp => AlMcp is not null;

    public sealed record AlMcpLaunch(string FileName, IReadOnlyList<string> LeadingArgs, string Description);

    public BcToolsLocator(string toolsDirectory)
    {
        ToolsDirectory = toolsDirectory;

        var nativeExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "almcp.exe" : "almcp";
        var nativePath = Path.Combine(toolsDirectory, nativeExe);
        var dllPath = Path.Combine(toolsDirectory, "almcp.dll");

        if (File.Exists(nativePath))
        {
            AlMcp = new AlMcpLaunch(nativePath, [], "native launcher");
            AlMcpPath = nativePath;
        }
        else if (File.Exists(dllPath))
        {
            var dotnetHost = DotnetHost.Resolve();
            AlMcp = new AlMcpLaunch(dotnetHost, [dllPath], "dotnet almcp.dll");
            AlMcpPath = dllPath;
        }
        else
        {
            AlMcpPath = nativePath;
        }

        var analyzersSubfolder = Path.Combine(toolsDirectory, "Analyzers");
        AnalyzerFolder = Directory.Exists(analyzersSubfolder) ? analyzersSubfolder : toolsDirectory;
    }

    /// <summary>
    /// Resolves the tools directory and registers an assembly resolver for it. Must be called at the
    /// very top of <c>Program.cs</c>: the BC DLLs are deliberately not in our output directory, so
    /// nothing that references a BC type may be JIT'd before this returns.
    /// </summary>
    public static BcToolsLocator ResolveAndRegister(string? explicitPath = null)
    {
        var locator = new BcToolsLocator(ResolveToolsDirectory(explicitPath));

        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name is null)
                return null;

            var candidate = Path.Combine(locator.ToolsDirectory, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };

        Console.Error.WriteLine($"BC DevTools: {locator.ToolsDirectory}");
        try
        {
            var version = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(Path.Combine(locator.ToolsDirectory, MarkerDll));
            Console.Error.WriteLine($"BC DevTools version: {version.FileVersion}");
        }
        catch { /* non-critical */ }

        if (locator.AlMcp is { } launch)
            Console.Error.WriteLine($"almcp: {launch.Description} ({launch.FileName})");
        else
            Console.Error.WriteLine("almcp: not found (16.2-and-earlier toolchain)");

        return locator;
    }

    /// <summary>
    /// The probe order, without the assembly-resolver side effect. Throws when nothing matches.
    /// </summary>
    public static string ResolveToolsDirectory(string? explicitPath = null)
    {
        var envPath = Environment.GetEnvironmentVariable("BCDEVELOPMENTTOOLSPATH");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var toolStoreRoot = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".dotnet", "tools", ".store", PackageId);
        return ResolveToolsDirectory(explicitPath, envPath, toolStoreRoot);
    }

    internal static string ResolveToolsDirectory(string? explicitPath, string? envPath, string? toolStoreRoot)
    {
        if (explicitPath is not null)
        {
            var resolved = Probe(explicitPath);
            if (resolved is not null)
                return Found("--devtools-path", resolved);

            throw new InvalidOperationException(
                $"--devtools-path: {DescribePath(explicitPath)}");
        }

        if (!string.IsNullOrEmpty(envPath) && Probe(envPath) is string fromEnv)
            return Found("BCDEVELOPMENTTOOLSPATH", fromEnv);

        if (TryDotnetToolStore(toolStoreRoot) is string fromStore)
            return Found("dotnet tool store", fromStore);

        var storeDir = toolStoreRoot ?? "(unknown)";
        var storeExists = toolStoreRoot is not null && Directory.Exists(toolStoreRoot);

        throw new InvalidOperationException(
            "BC Development Tools not found. Probed locations:\n" +
            $"  --devtools-path:        (not supplied)\n" +
            $"  BCDEVELOPMENTTOOLSPATH:  {DescribePath(envPath)}\n" +
            $"  dotnet tool store:      {storeDir} ({(storeExists ? "exists, but no supported version found" : "does not exist")})\n\n" +
            "Install the BC Development Tools with:\n" +
            "  dotnet tool install -g Microsoft.Dynamics.BusinessCentral.Development.Tools\n\n" +
            "Or point at an existing installation:\n" +
            "  --devtools-path <dir>   (directory containing " + MarkerDll + ")\n" +
            "  BCDEVELOPMENTTOOLSPATH=<dir>");
    }

    private static string DescribePath(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(unset)";
        if (!Directory.Exists(value))
            return $"'{value}' (directory does not exist)";
        return $"'{value}' (no {MarkerDll} in the directory, <dir>/<tfm>/ or <dir>/tools/<tfm>/any/)";
    }

    private static string Found(string source, string path)
    {
        Console.Error.WriteLine($"BC DevTools resolved via {source}: {path}");
        return path;
    }

    /// <summary>
    /// Accepts a directory holding the DLLs directly, or one of the two packaged layouts above it.
    /// </summary>
    private static string? Probe(string root)
    {
        if (HasMarker(root))
            return root;

        foreach (var tfm in TfmSubfolders)
        {
            if (HasMarker(Path.Combine(root, tfm)))
                return Path.Combine(root, tfm);

            var nupkgLayout = Path.Combine(root, "tools", tfm, "any");
            if (HasMarker(nupkgLayout))
                return nupkgLayout;
        }

        return null;
    }

    private static string? TryDotnetToolStore(string? storeRoot)
    {
        if (storeRoot is null || !Directory.Exists(storeRoot))
            return null;

        // Layout: .store/<pkg>/<ver>/<pkg>/<ver>/tools/<tfm>/any/
        foreach (var versionDir in EnumerateByDescendingVersion(storeRoot, Path.GetFileName))
        {
            var packageDir = Path.Combine(versionDir, PackageId, Path.GetFileName(versionDir));
            if (Probe(packageDir) is string resolved)
                return resolved;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateByDescendingVersion(string root, Func<string, string> versionOf)
        => OrderByDescendingVersion(SafeEnumerateDirectories(root, "*"), versionOf);

    /// <summary>
    /// Highest version wins using full SemVer 2 ordering (prerelease identifiers compared
    /// numerically where possible). Unparseable names sort last rather than being dropped.
    /// </summary>
    internal static IEnumerable<string> OrderByDescendingVersion(IEnumerable<string> paths, Func<string, string> versionOf)
        => paths
            .Select(p => (Path: p, Version: SemanticVersion.TryParse(versionOf(p), out var v) ? v : null))
            .OrderByDescending(x => x.Version, SemanticVersion.Comparer)
            .Select(x => x.Path);

    internal static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern = "*")
    {
        try
        {
            return Directory.EnumerateDirectories(root, pattern);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static bool HasMarker(string directory)
        => Directory.Exists(directory) && File.Exists(Path.Combine(directory, MarkerDll));
}
