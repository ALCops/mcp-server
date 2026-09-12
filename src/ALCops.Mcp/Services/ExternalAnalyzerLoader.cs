using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

public sealed class ExternalAnalyzerLoader
{
    private readonly BcToolsLocator _toolsLocator;
    private readonly ConcurrentDictionary<string, LoadedAnalyzerAssembly> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _assemblyResolveRegistered;

    public ExternalAnalyzerLoader(BcToolsLocator toolsLocator)
    {
        _toolsLocator = toolsLocator;
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
    /// Resolves an analyzer spec to a DLL path without loading it. Used at startup to compose the
    /// child <c>almcp</c>'s <c>--codeanalyzers</c> list.
    /// </summary>
    public string? ResolveDllPath(AnalyzerSpec spec, string projectPath)
    {
        switch (spec.Kind)
        {
            case AnalyzerSpecKind.WellKnownBcCop:
            case AnalyzerSpecKind.AnalyzerFolderRelative:
            {
                var candidate = Path.Combine(_toolsLocator.AnalyzerFolder, spec.GetDllFileName());
                if (File.Exists(candidate))
                    return candidate;

                // Fallback: project-local .vscode/analyzers/
                var localPath = Path.Combine(projectPath, ".vscode", "analyzers", spec.GetDllFileName());
                return File.Exists(localPath) ? localPath : null;
            }

            case AnalyzerSpecKind.DllPath:
                return Path.IsPathRooted(spec.RawValue)
                    ? spec.RawValue
                    : Path.Combine(projectPath, spec.RawValue);

            default:
                return null;
        }
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
        if (Interlocked.CompareExchange(ref _assemblyResolveRegistered, 1, 0) != 0)
            return;

        string[] searchPaths = [_toolsLocator.AnalyzerFolder, _toolsLocator.ToolsDirectory, AppContext.BaseDirectory];

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var dllName = new AssemblyName(args.Name).Name + ".dll";

            foreach (var dir in searchPaths)
            {
                var candidate = Path.Combine(dir, dllName);
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }

            return null;
        };
    }
}
