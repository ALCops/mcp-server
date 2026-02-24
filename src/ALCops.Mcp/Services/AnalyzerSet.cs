using System.Collections.Immutable;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

public sealed class AnalyzerSet : IAnalyzerProvider
{
    private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private readonly ImmutableArray<CodeFixProvider> _codeFixProviders;
    private readonly ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> _diagnosticToFixProviders;
    private readonly ImmutableDictionary<string, DiagnosticDescriptor> _descriptors;
    private readonly ImmutableDictionary<string, string> _diagnosticToCopName;

    public IReadOnlyList<string> Warnings { get; }
    public Dictionary<string, RuleAction>? RuleActions { get; }

    public AnalyzerSet(AnalyzerRegistry builtIn, IReadOnlyList<LoadedAnalyzerAssembly> externalAssemblies, IReadOnlyList<string>? warnings = null, Dictionary<string, RuleAction>? ruleActions = null)
    {
        Warnings = warnings ?? [];
        RuleActions = ruleActions;

        // Merge analyzers: built-in first, then external
        var analyzers = builtIn.GetAllAnalyzers().ToBuilder();
        foreach (var ext in externalAssemblies)
            analyzers.AddRange(ext.Analyzers);
        _analyzers = analyzers.ToImmutable();

        // Merge code fix providers
        var fixProviders = builtIn.GetAllCodeFixProviders().ToBuilder();
        foreach (var ext in externalAssemblies)
            fixProviders.AddRange(ext.CodeFixProviders);
        _codeFixProviders = fixProviders.ToImmutable();

        // Merge descriptors: built-in takes precedence
        var descriptors = new Dictionary<string, DiagnosticDescriptor>(builtIn.GetAllDescriptors());
        foreach (var ext in externalAssemblies)
        {
            foreach (var (id, descriptor) in ext.Descriptors)
                descriptors.TryAdd(id, descriptor);
        }
        _descriptors = descriptors.ToImmutableDictionary();

        // Merge cop names: built-in takes precedence
        var copNames = new Dictionary<string, string>();
        foreach (var (id, _) in builtIn.GetAllDescriptors())
            copNames[id] = builtIn.GetCopName(id);
        foreach (var ext in externalAssemblies)
        {
            foreach (var (id, _) in ext.Descriptors)
                copNames.TryAdd(id, ext.CopName);
        }
        _diagnosticToCopName = copNames.ToImmutableDictionary();

        // Merge fix provider lookup: aggregate all providers per diagnostic
        var diagnosticToFix = new Dictionary<string, List<CodeFixProvider>>();
        AddFixProviders(diagnosticToFix, builtIn.GetAllCodeFixProviders());
        foreach (var ext in externalAssemblies)
            AddFixProviders(diagnosticToFix, ext.CodeFixProviders);
        _diagnosticToFixProviders = diagnosticToFix.ToImmutableDictionary(
            kv => kv.Key, kv => kv.Value.ToImmutableArray());
    }

    private static void AddFixProviders(Dictionary<string, List<CodeFixProvider>> map, ImmutableArray<CodeFixProvider> providers)
    {
        foreach (var provider in providers)
        {
            foreach (var id in provider.FixableDiagnosticIds)
            {
                if (!map.TryGetValue(id, out var list))
                {
                    list = [];
                    map[id] = list;
                }
                list.Add(provider);
            }
        }
    }

    public ImmutableArray<DiagnosticAnalyzer> GetAllAnalyzers() => _analyzers;
    public ImmutableArray<CodeFixProvider> GetAllCodeFixProviders() => _codeFixProviders;

    public ImmutableArray<CodeFixProvider> GetCodeFixProvidersForDiagnostic(string diagnosticId)
        => _diagnosticToFixProviders.TryGetValue(diagnosticId, out var providers)
            ? providers
            : ImmutableArray<CodeFixProvider>.Empty;

    public ImmutableDictionary<string, DiagnosticDescriptor> GetAllDescriptors() => _descriptors;

    public string GetCopName(string diagnosticId)
        => _diagnosticToCopName.TryGetValue(diagnosticId, out var name) ? name : "Unknown";

    public bool HasCodeFix(string diagnosticId) => _diagnosticToFixProviders.ContainsKey(diagnosticId);
}
