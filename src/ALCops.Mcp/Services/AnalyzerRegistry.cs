using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;

namespace ALCops.Mcp.Services;

public sealed class AnalyzerRegistry : IAnalyzerProvider
{
    private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private readonly ImmutableArray<CodeFixProvider> _codeFixProviders;
    private readonly ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> _diagnosticToFixProviders;
    private readonly ImmutableDictionary<string, DiagnosticDescriptor> _diagnosticDescriptors;
    private readonly ImmutableDictionary<string, string> _diagnosticToCopName;

    private static readonly string[] CopAssemblyNames =
    [
        "ALCops.ApplicationCop",
        "ALCops.DocumentationCop",
        "ALCops.FormattingCop",
        "ALCops.LinterCop",
        "ALCops.PlatformCop",
        "ALCops.TestAutomationCop"
    ];

    public AnalyzerRegistry()
    {
        var analyzers = new List<DiagnosticAnalyzer>();
        var fixProviders = new List<CodeFixProvider>();
        var diagnosticToFix = new Dictionary<string, List<CodeFixProvider>>();
        var descriptors = new Dictionary<string, DiagnosticDescriptor>();
        var diagnosticToCop = new Dictionary<string, string>();

        foreach (var assemblyName in CopAssemblyNames)
        {
            var copName = assemblyName.Replace("ALCops.", "");

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(new AssemblyName(assemblyName));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to load assembly {assemblyName}: {ex.Message}");
                continue;
            }

            foreach (var type in assembly.GetExportedTypes())
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
                        {
                            descriptors.TryAdd(descriptor.Id, descriptor);
                            diagnosticToCop.TryAdd(descriptor.Id, copName);
                        }
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
                        var provider = (CodeFixProvider)Activator.CreateInstance(type)!;
                        fixProviders.Add(provider);

                        foreach (var id in provider.FixableDiagnosticIds)
                        {
                            if (!diagnosticToFix.TryGetValue(id, out var list))
                            {
                                list = [];
                                diagnosticToFix[id] = list;
                            }
                            list.Add(provider);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Warning: Failed to instantiate code fix provider {type.Name}: {ex.Message}");
                    }
                }
            }
        }

        _analyzers = [.. analyzers];
        _codeFixProviders = [.. fixProviders];
        _diagnosticToFixProviders = diagnosticToFix.ToImmutableDictionary(
            kv => kv.Key, kv => kv.Value.ToImmutableArray());
        _diagnosticDescriptors = descriptors.ToImmutableDictionary();
        _diagnosticToCopName = diagnosticToCop.ToImmutableDictionary();
    }

    public ImmutableArray<DiagnosticAnalyzer> GetAllAnalyzers() => _analyzers;

    public ImmutableArray<CodeFixProvider> GetAllCodeFixProviders() => _codeFixProviders;

    public ImmutableArray<CodeFixProvider> GetCodeFixProvidersForDiagnostic(string diagnosticId)
        => _diagnosticToFixProviders.TryGetValue(diagnosticId, out var providers)
            ? providers
            : ImmutableArray<CodeFixProvider>.Empty;

    public ImmutableDictionary<string, DiagnosticDescriptor> GetAllDescriptors() => _diagnosticDescriptors;

    public string GetCopName(string diagnosticId)
        => _diagnosticToCopName.TryGetValue(diagnosticId, out var name) ? name : "Unknown";

    public bool HasCodeFix(string diagnosticId) => _diagnosticToFixProviders.ContainsKey(diagnosticId);
}
