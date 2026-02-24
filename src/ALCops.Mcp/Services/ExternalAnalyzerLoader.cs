using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

public sealed class ExternalAnalyzerLoader
{
    private readonly AlExtensionLocator _alExtensionLocator;
    private readonly NuGetDevToolsDownloader _nugetDownloader;
    private readonly DevToolsLocator _devToolsLocator;
    private readonly ConcurrentDictionary<string, LoadedAnalyzerAssembly> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _assemblyResolveRegistered;

    // Lazily resolved fallback path for BC cop DLLs (NuGet download)
    private string? _nugetToolsPath;
    private bool _nugetToolsResolved;

    public ExternalAnalyzerLoader(
        AlExtensionLocator alExtensionLocator,
        NuGetDevToolsDownloader nugetDownloader,
        DevToolsLocator devToolsLocator)
    {
        _alExtensionLocator = alExtensionLocator;
        _nugetDownloader = nugetDownloader;
        _devToolsLocator = devToolsLocator;
    }

    public LoadedAnalyzerAssembly? ResolveAndLoad(AnalyzerSpec spec, string projectPath)
    {
        var dllPath = ResolveDllPath(spec, projectPath);
        if (dllPath is null || !File.Exists(dllPath))
        {
            Console.Error.WriteLine($"Warning: Analyzer DLL not found: {spec.RawValue} (resolved to: {dllPath ?? "null"})");
            return null;
        }

        var fullPath = Path.GetFullPath(dllPath);
        return _cache.GetOrAdd(fullPath, path => LoadAssembly(path, spec));
    }

    /// <summary>
    /// Async variant that allows NuGet download fallback.
    /// </summary>
    public async Task<LoadedAnalyzerAssembly?> ResolveAndLoadAsync(AnalyzerSpec spec, string projectPath, CancellationToken ct = default)
    {
        var dllPath = ResolveDllPath(spec, projectPath);

        // If sync resolution failed for BC cops, try NuGet download
        if ((dllPath is null || !File.Exists(dllPath))
            && (spec.Kind == AnalyzerSpecKind.WellKnownBcCop || spec.Kind == AnalyzerSpecKind.AnalyzerFolderRelative))
        {
            await EnsureNuGetToolsResolvedAsync(ct);
            if (_nugetToolsPath is not null)
            {
                var candidate = Path.Combine(_nugetToolsPath, spec.GetDllFileName());
                if (File.Exists(candidate))
                    dllPath = candidate;
            }
        }

        if (dllPath is null || !File.Exists(dllPath))
        {
            Console.Error.WriteLine($"Warning: Analyzer DLL not found: {spec.RawValue} (resolved to: {dllPath ?? "null"})");
            return null;
        }

        var fullPath = Path.GetFullPath(dllPath);
        return _cache.GetOrAdd(fullPath, path => LoadAssembly(path, spec));
    }

    private string? ResolveDllPath(AnalyzerSpec spec, string projectPath)
    {
        switch (spec.Kind)
        {
            case AnalyzerSpecKind.WellKnownBcCop:
                return ResolveBcCopPath(spec.GetDllFileName());

            case AnalyzerSpecKind.AnalyzerFolderRelative:
            {
                // ${analyzerFolder} → AL extension's Analyzers directory
                var extensionPath = _alExtensionLocator.GetAnalyzersPath();
                if (extensionPath is not null)
                {
                    var candidate = Path.Combine(extensionPath, spec.GetDllFileName());
                    if (File.Exists(candidate))
                        return candidate;
                }

                // Fallback: project-local .vscode/analyzers/
                var localPath = Path.Combine(projectPath, ".vscode", "analyzers", spec.GetDllFileName());
                if (File.Exists(localPath))
                    return localPath;

                // Try NuGet cache (if already resolved)
                if (_nugetToolsPath is not null)
                {
                    var nugetCandidate = Path.Combine(_nugetToolsPath, spec.GetDllFileName());
                    if (File.Exists(nugetCandidate))
                        return nugetCandidate;
                }

                return null;
            }

            case AnalyzerSpecKind.DllPath:
                return Path.IsPathRooted(spec.RawValue)
                    ? spec.RawValue
                    : Path.Combine(projectPath, spec.RawValue);

            default:
                return null;
        }
    }

    private string? ResolveBcCopPath(string dllFileName)
    {
        // 1. AL VS Code extension (matches user's dev environment)
        var extensionPath = _alExtensionLocator.GetAnalyzersPath();
        if (extensionPath is not null)
        {
            var candidate = Path.Combine(extensionPath, dllFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. NuGet cache (if already resolved synchronously)
        if (_nugetToolsPath is not null)
        {
            var candidate = Path.Combine(_nugetToolsPath, dllFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        // 3. DevTools directory (CI/build environments)
        try
        {
            var devToolsPath = Path.Combine(_devToolsLocator.GetDevToolsPath(), "net8.0", dllFileName);
            if (File.Exists(devToolsPath))
                return devToolsPath;
        }
        catch { /* DevTools not available */ }

        return null;
    }

    private async Task EnsureNuGetToolsResolvedAsync(CancellationToken ct)
    {
        if (_nugetToolsResolved)
            return;

        _nugetToolsResolved = true;
        _nugetToolsPath = await _nugetDownloader.GetToolsPathAsync(ct);
    }

    private LoadedAnalyzerAssembly LoadAssembly(string fullPath, AnalyzerSpec spec)
    {
        EnsureAssemblyResolveRegistered();

        var copName = spec.CopName ?? Path.GetFileNameWithoutExtension(fullPath);
        try
        {
            // Use a custom AssemblyLoadContext that resolves shared types
            // (DiagnosticAnalyzer, CodeFixProvider, etc.) from the host context.
            // Without this, the cop DLL loads its own copy of Nav.CodeAnalysis
            // and typeof(DiagnosticAnalyzer).IsAssignableFrom() returns false.
            var loadContext = new AnalyzerAssemblyLoadContext(fullPath);
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            return ScanAssembly(assembly, copName, fullPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load analyzer DLL {fullPath}: {ex.Message}");
            return new LoadedAnalyzerAssembly(
                copName, fullPath,
                ImmutableArray<DiagnosticAnalyzer>.Empty,
                ImmutableArray<CodeFixProvider>.Empty,
                ImmutableDictionary<string, DiagnosticDescriptor>.Empty);
        }
    }

    private static LoadedAnalyzerAssembly ScanAssembly(Assembly assembly, string copName, string dllPath)
    {
        var analyzers = new List<DiagnosticAnalyzer>();
        var fixProviders = new List<CodeFixProvider>();
        var descriptors = new Dictionary<string, DiagnosticDescriptor>();

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types couldn't load (missing dependencies) — use the ones that did
            types = ex.Types.Where(t => t is not null).ToArray()!;
            foreach (var loaderEx in ex.LoaderExceptions.Where(e => e is not null).DistinctBy(e => e!.Message))
                Console.Error.WriteLine($"Warning: Partial load of {copName}: {loaderEx!.Message}");
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            if (typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            {
                try
                {
                    var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;
                    analyzers.Add(analyzer);

                    foreach (var descriptor in analyzer.SupportedDiagnostics)
                        descriptors.TryAdd(descriptor.Id, descriptor);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to instantiate analyzer {type.Name}: {ex.Message}");
                }
            }

            if (typeof(CodeFixProvider).IsAssignableFrom(type))
            {
                try
                {
                    fixProviders.Add((CodeFixProvider)Activator.CreateInstance(type)!);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to instantiate code fix provider {type.Name}: {ex.Message}");
                }
            }
        }

        return new LoadedAnalyzerAssembly(
            copName, dllPath,
            [.. analyzers],
            [.. fixProviders],
            descriptors.ToImmutableDictionary());
    }

    private void EnsureAssemblyResolveRegistered()
    {
        if (Interlocked.CompareExchange(ref _assemblyResolveRegistered, 1, 0) == 0)
        {
            // Build search paths: AL extension → NuGet cache → DevTools → exe dir
            var searchPaths = new List<string>();

            var extensionPath = _alExtensionLocator.GetAnalyzersPath();
            if (extensionPath is not null)
                searchPaths.Add(extensionPath);

            if (_nugetToolsPath is not null)
                searchPaths.Add(_nugetToolsPath);

            try { searchPaths.Add(Path.Combine(_devToolsLocator.GetDevToolsPath(), "net8.0")); }
            catch { /* DevTools not available */ }

            searchPaths.Add(AppContext.BaseDirectory);

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var assemblyName = new AssemblyName(args.Name);
                var dllName = assemblyName.Name + ".dll";

                // Also check NuGet path if it was resolved after registration
                var paths = _nugetToolsPath is not null && !searchPaths.Contains(_nugetToolsPath)
                    ? searchPaths.Append(_nugetToolsPath)
                    : searchPaths;

                foreach (var dir in paths)
                {
                    var candidate = Path.Combine(dir, dllName);
                    if (File.Exists(candidate))
                        return Assembly.LoadFrom(candidate);
                }

                return null;
            };
        }
    }
}
