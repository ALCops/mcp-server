using System.Collections.Immutable;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;

namespace ALCops.Mcp.Services;

public interface IAnalyzerProvider
{
    ImmutableArray<DiagnosticAnalyzer> GetAllAnalyzers();
    ImmutableArray<CodeFixProvider> GetAllCodeFixProviders();
    ImmutableArray<CodeFixProvider> GetCodeFixProvidersForDiagnostic(string diagnosticId);
    ImmutableDictionary<string, DiagnosticDescriptor> GetAllDescriptors();
    string GetCopName(string diagnosticId);
    bool HasCodeFix(string diagnosticId);
}
