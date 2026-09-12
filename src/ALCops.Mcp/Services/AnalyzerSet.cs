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

    public AnalyzerSet(IReadOnlyList<LoadedAnalyzerAssembly> assemblies, IReadOnlyList<string>? warnings = null, Dictionary<string, RuleAction>? ruleActions = null)
    {
        Warnings = warnings ?? [];
        RuleActions = ruleActions;

        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var fixProviders = ImmutableArray.CreateBuilder<CodeFixProvider>();
        var descriptors = new Dictionary<string, DiagnosticDescriptor>();
        var copNames = new Dictionary<string, string>();
        var diagnosticToFix = new Dictionary<string, List<CodeFixProvider>>();

        // First assembly to claim a diagnostic ID wins, so the configured order in
        // al.codeAnalyzers decides which cop owns an ID when two of them declare the same one.
        foreach (var assembly in assemblies)
        {
            analyzers.AddRange(assembly.Analyzers);
            fixProviders.AddRange(assembly.CodeFixProviders);

            foreach (var (id, descriptor) in assembly.Descriptors)
            {
                descriptors.TryAdd(id, descriptor);
                copNames.TryAdd(id, assembly.CopName);
            }

            AddFixProviders(diagnosticToFix, assembly.CodeFixProviders);
        }

        _analyzers = analyzers.ToImmutable();
        _codeFixProviders = fixProviders.ToImmutable();
        _descriptors = descriptors.ToImmutableDictionary();
        _diagnosticToCopName = copNames.ToImmutableDictionary();
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
