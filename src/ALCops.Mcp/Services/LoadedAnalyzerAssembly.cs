using System.Collections.Immutable;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

public sealed record LoadedAnalyzerAssembly(
    string CopName,
    string DllPath,
    ImmutableArray<DiagnosticAnalyzer> Analyzers,
    ImmutableArray<CodeFixProvider> CodeFixProviders,
    ImmutableDictionary<string, DiagnosticDescriptor> Descriptors);
