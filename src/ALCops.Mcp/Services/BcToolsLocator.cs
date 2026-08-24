using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ALCops.Mcp.Services;

/// <summary>
/// Locates the single directory that holds both the BC DevTools assemblies
/// (<c>Microsoft.Dynamics.Nav.*.dll</c>) and Microsoft's <c>almcp</c> executable. Both delivery
/// channels ship them side by side — the AL VS Code extension's <c>bin/</c> and the dotnet tool's
/// <c>tools/&lt;tfm&gt;/any/</c> — so one locator serves both. That co-location is also what keeps
/// our in-process code fixes loading the *same* <c>Nav.CodeAnalysis</c> the child <c>almcp</c> uses.
/// </summary>
public sealed class BcToolsLocator
{
    private const string MarkerDll = "Microsoft.Dynamics.Nav.CodeAnalysis.dll";
    private const string PackageId = "microsoft.dynamics.businesscentral.development.tools";

    // Probed in order; net10.0 first so an SDK shipping both is used at our own runtime version.
    private static readonly string[] TfmSubfolders = ["net10.0", "net8.0"];

    /// <summary>The directory holding the BC DevTools DLLs and <c>almcp</c>.</summary>
    public string ToolsDirectory { get; }

    /// <summary>Full path to <c>almcp[.exe]</c>. May not exist on 16.2-and-earlier toolchains.</summary>
    public string AlMcpPath { get; }

    /// <summary>
    /// Where <c>${CodeCop}</c> / <c>${analyzerFolder}</c> specs resolve to: the AL extension's
    /// <c>Analyzers/</c> subfolder when present, otherwise the flat tools directory.
    /// </summary>
    public string AnalyzerFolder { get; }

    public bool HasAlMcp => File.Exists(AlMcpPath);

    public BcToolsLocator(string toolsDirectory)
    {
        ToolsDirectory = toolsDirectory;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "almcp.exe" : "almcp";
        AlMcpPath = Path.Combine(toolsDirectory, exeName);

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

        return locator;
    }

    /// <summary>
    /// The probe order, without the assembly-resolver side effect. Throws when nothing matches.
    /// </summary>
    public static string ResolveToolsDirectory(string? explicitPath = null)
    {
        if (explicitPath is not null)
        {
            var resolved = Probe(explicitPath);
            if (resolved is not null)
                return Found("--devtools-path", resolved);

            throw new InvalidOperationException(
                $"--devtools-path '{explicitPath}' does not contain {MarkerDll} (checked the directory itself, " +
                $"<dir>/<tfm>/ and <dir>/tools/<tfm>/any/).");
        }

        var envPath = Environment.GetEnvironmentVariable("BCDEVELOPMENTTOOLSPATH");
        if (!string.IsNullOrEmpty(envPath) && Probe(envPath) is string fromEnv)
            return Found("BCDEVELOPMENTTOOLSPATH", fromEnv);

        if (TryDotnetToolStore() is string fromStore)
            return Found("dotnet tool store", fromStore);

        if (TryAlExtension() is string fromExtension)
            return Found("AL VS Code extension", fromExtension);

        throw new InvalidOperationException(
            "BC Development Tools not found. Install them with:\n" +
            "  dotnet tool install -g Microsoft.Dynamics.BusinessCentral.Development.Tools\n" +
            "Alternatively install the AL Language extension for VS Code, set BCDEVELOPMENTTOOLSPATH, " +
            "or pass --devtools-path <dir>.");
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

    private static string? TryDotnetToolStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

        var storeRoot = Path.Combine(home, ".dotnet", "tools", ".store", PackageId);
        if (!Directory.Exists(storeRoot))
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

    private static string? TryAlExtension()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

        const string prefix = "ms-dynamics-smb.al-";
        string[] extensionRoots =
        [
            Path.Combine(home, ".vscode", "extensions"),
            Path.Combine(home, ".vscode-insiders", "extensions"),
            Path.Combine(home, ".vscode-server", "extensions"),
        ];

        var candidates = extensionRoots
            .Where(Directory.Exists)
            .SelectMany(root => SafeEnumerateDirectories(root, prefix + "*"));

        foreach (var extensionDir in OrderByDescendingVersion(candidates, d => Path.GetFileName(d)[prefix.Length..]))
        {
            if (Probe(Path.Combine(extensionDir, "bin")) is string resolved)
                return resolved;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateByDescendingVersion(string root, Func<string, string> versionOf)
        => OrderByDescendingVersion(SafeEnumerateDirectories(root, "*"), versionOf);

    /// <summary>
    /// Highest version wins; a stable release outranks a prerelease of the same base version.
    /// Unparseable names sort last rather than being dropped.
    /// </summary>
    private static IEnumerable<string> OrderByDescendingVersion(IEnumerable<string> paths, Func<string, string> versionOf)
        => paths
            .Select(p =>
            {
                var raw = versionOf(p);
                var dashIndex = raw.IndexOf('-');
                var isStable = dashIndex < 0;
                var baseVersion = isStable ? raw : raw[..dashIndex];
                return (Path: p, Version: Version.TryParse(baseVersion, out var v) ? v : null, IsStable: isStable);
            })
            .OrderByDescending(x => x.Version is not null)
            .ThenByDescending(x => x.Version)
            .ThenByDescending(x => x.IsStable)
            .Select(x => x.Path);

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
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
