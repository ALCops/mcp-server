using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging;

namespace ALCops.Mcp.Services;

/// <summary>
/// Resolved once at startup: which AL projects this server is working on, and the analyzer/ruleset
/// configuration those projects declare.
/// </summary>
public sealed record WorkspaceStartupConfig(
    IReadOnlyList<string> ProjectDirectories,
    IReadOnlyList<string> AnalyzerDllPaths,
    string? RulesetPath)
{
    public string? PrimaryProject => ProjectDirectories.Count > 0 ? ProjectDirectories[0] : null;
}

/// <summary>
/// Bridges the project's own analyzer configuration into the child <c>almcp</c>.
///
/// <para><c>almcp</c> in MCP mode never reads <c>.vscode/settings.json</c> — its
/// <c>WorkspaceSettingsApplier</c> runs in LSP mode only — and there is no per-call ruleset or
/// analyzer parameter. So a child <c>almcp</c> starts with zero analyzers unless we tell it which
/// ones to load, at launch. Resolving that here (rather than rewriting per-tool arguments) is what
/// keeps <c>al_compile</c> and our own fix tools agreeing about which rules apply and which are
/// suppressed.</para>
/// </summary>
public sealed class WorkspaceStartupResolver
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".alpackages", "bin", "obj", ".git", ".vs", "node_modules", "packages"
    };

    private const int MaxScanDepth = 4;

    private readonly ProjectAnalyzerResolver _analyzerResolver;
    private readonly ExternalAnalyzerLoader _loader;
    private readonly ILogger<WorkspaceStartupResolver> _logger;
    private readonly string[]? _explicitProjects;
    private readonly Lazy<WorkspaceStartupConfig> _config;

    public WorkspaceStartupResolver(
        ProjectAnalyzerResolver analyzerResolver,
        ExternalAnalyzerLoader loader,
        ILogger<WorkspaceStartupResolver> logger,
        string[]? explicitProjects = null)
    {
        _analyzerResolver = analyzerResolver;
        _loader = loader;
        _logger = logger;
        _explicitProjects = explicitProjects;
        _config = new Lazy<WorkspaceStartupConfig>(Resolve);
    }

    public WorkspaceStartupConfig Config => _config.Value;

    /// <summary>
    /// Composes the child <c>almcp</c> argument list, merged with any passthrough args the user gave
    /// us. User-supplied flags always win — we only fill in what they left unset.
    /// </summary>
    public string[] BuildAlMcpArgs(IReadOnlyList<string> userArgs)
    {
        var config = Config;
        var userFlags = userArgs
            .Where(a => a.StartsWith("--", StringComparison.Ordinal))
            .Select(a => a.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var args = new List<string>();

        void AddIfUnset(string flag, string value)
        {
            if (!userFlags.Contains(flag))
            {
                args.Add(flag);
                args.Add(value);
            }
        }

        // almcp splits both of these on ';'.
        if (config.ProjectDirectories.Count > 0)
            AddIfUnset("--projects", string.Join(';', config.ProjectDirectories));

        if (config.AnalyzerDllPaths.Count > 0)
        {
            AddIfUnset("--enablecodeanalysis", "true");
            AddIfUnset("--codeanalyzers", string.Join(';', config.AnalyzerDllPaths));
        }

        if (config.RulesetPath is not null)
            AddIfUnset("--rulesetpath", config.RulesetPath);

        args.AddRange(userArgs);
        return [.. args];
    }

    private WorkspaceStartupConfig Resolve()
    {
        var projects = _explicitProjects is { Length: > 0 }
            ? [.. _explicitProjects.Select(Path.GetFullPath)]
            : DiscoverProjects(Directory.GetCurrentDirectory());

        if (projects.Count == 0)
        {
            // Not fatal: almcp prints its own "use the al_addproject tool" hint, and every native
            // tool takes projectPath per call. A silently wrong cwd guess is the only bad failure
            // mode here, which is exactly why all of this is logged.
            _logger.LogWarning(
                "No AL projects found under {Cwd} (scanned {Depth} levels for app.json). " +
                "Pass --projects <dir>[;<dir>] to set them explicitly.",
                Directory.GetCurrentDirectory(), MaxScanDepth);
            return new WorkspaceStartupConfig([], [], null);
        }

        _logger.LogInformation("Discovered {Count} AL project(s): {Projects}",
            projects.Count, string.Join(", ", projects));

        // The first project's configuration drives the child almcp — it has one global analyzer set.
        var primary = projects[0];
        if (projects.Count > 1)
            _logger.LogInformation("Using analyzer/ruleset configuration from {Project}", primary);

        var settingsPath = Path.Combine(primary, ".vscode", "settings.json");
        _logger.LogInformation("Workspace settings: {Path}",
            File.Exists(settingsPath) ? settingsPath : $"{settingsPath} (not present)");

        var analyzerPaths = new List<string>();
        foreach (var rawSpec in _analyzerResolver.GetConfiguredAnalyzerSpecs(primary) ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawSpec))
                continue;

            var resolved = _loader.ResolveDllPath(AnalyzerSpec.Parse(rawSpec), primary);
            if (resolved is null || !File.Exists(resolved))
            {
                _logger.LogWarning("Analyzer {Spec} could not be resolved to an existing DLL; skipped.", rawSpec);
                continue;
            }

            _logger.LogInformation("Analyzer {Spec} -> {Path}", rawSpec, resolved);
            AddDistinct(analyzerPaths, Path.GetFullPath(resolved));

            // almcp turns each --codeanalyzers entry into an AnalyzerFileReference and resolves
            // dependencies only among the paths it was given — it does not probe the analyzer's own
            // directory. Anything left out surfaces as AD0001 "analyzer threw FileNotFoundException"
            // instead of the rule's diagnostics, so the dependencies have to travel with it.
            foreach (var dependency in SiblingDependencies(resolved))
            {
                _logger.LogInformation("  dependency of {Spec}: {Path}", rawSpec, dependency);
                AddDistinct(analyzerPaths, dependency);
            }
        }

        if (analyzerPaths.Count == 0)
            _logger.LogWarning(
                "No analyzers configured for {Project}. al_compile will report compiler diagnostics only. " +
                "Configure al.codeAnalyzers in .vscode/settings.json to enable cops.", primary);

        var rulesetPath = _analyzerResolver.GetConfiguredRulesetPath(primary);
        _logger.LogInformation("Ruleset: {Path}", rulesetPath ?? "(none)");

        return new WorkspaceStartupConfig(projects, analyzerPaths, rulesetPath);
    }

    private static void AddDistinct(List<string> paths, string path)
    {
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            paths.Add(path);
    }

    /// <summary>
    /// The assemblies an analyzer DLL references that sit next to it — e.g. <c>ALCops.Common.dll</c>
    /// beside the ALCops cops, or <c>Microsoft.Dynamics.Nav.Analyzers.Common.dll</c> beside CodeCop.
    /// Read from metadata rather than loaded, so this costs nothing and cannot fail on a bad DLL.
    /// </summary>
    private static IEnumerable<string> SiblingDependencies(string analyzerDll)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(analyzerDll));
        if (directory is null)
            return [];

        try
        {
            using var stream = File.OpenRead(analyzerDll);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return [];

            var metadata = peReader.GetMetadataReader();
            return
            [
                .. metadata.AssemblyReferences
                    .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                    .Where(IsCandidateDependency)
                    .Select(name => Path.Combine(directory, name + ".dll"))
                    .Where(File.Exists)
            ];
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Framework assemblies come from the runtime, and the compiler's own assemblies are already
    /// loaded by the host — re-registering either as an analyzer reference would at best be noise.
    /// </summary>
    private static bool IsCandidateDependency(string assemblyName)
        => !assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        && !assemblyName.StartsWith("Microsoft.Dynamics.Nav.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
        && assemblyName is not ("mscorlib" or "netstandard");

    /// <summary>
    /// Mirrors <c>almcp</c>'s own LSP-mode discovery (<c>AgenticInitializeRequestHandler</c>):
    /// a root that is itself a project wins outright, otherwise scan downward for app.json.
    /// </summary>
    public static List<string> DiscoverProjects(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return [];

        if (File.Exists(Path.Combine(rootPath, "app.json")))
            return [Path.GetFullPath(rootPath)];

        var found = new List<string>();
        Scan(rootPath, 0, found);
        return found;
    }

    private static void Scan(string directory, int depth, List<string> found)
    {
        if (depth >= MaxScanDepth)
            return;

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var subdirectory in subdirectories)
        {
            if (ExcludedDirectoryNames.Contains(Path.GetFileName(subdirectory)))
                continue;

            if (File.Exists(Path.Combine(subdirectory, "app.json")))
                found.Add(Path.GetFullPath(subdirectory));
            else
                Scan(subdirectory, depth + 1, found);
        }
    }
}
