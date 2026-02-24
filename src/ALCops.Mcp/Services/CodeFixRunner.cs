using System.Collections.Immutable;
using ALCops.Mcp.Models;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeActions;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;

namespace ALCops.Mcp.Services;

public sealed class CodeFixRunner
{
    private readonly AnalyzerRegistry _registry;

    public CodeFixRunner(AnalyzerRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Gets available code fixes for a specific diagnostic at a location.
    /// </summary>
    public async Task<IReadOnlyList<CodeFixInfo>> GetFixesAsync(
        ProjectSession session,
        string filePath,
        string diagnosticId,
        int line,
        int column,
        CancellationToken ct = default,
        IAnalyzerProvider? analyzerProvider = null)
    {
        var provider = analyzerProvider ?? _registry;
        var providers = provider.GetCodeFixProvidersForDiagnostic(diagnosticId);
        if (providers.IsEmpty)
            return [];

        var document = session.GetDocument(filePath);
        if (document is null)
            return [];

        // Find the diagnostic at the specified location
        var diagnostic = await FindDiagnosticAsync(session, document, diagnosticId, line, column, ct, provider);
        if (diagnostic is null)
            return [];

        // Collect code actions from all providers
        var fixes = new List<CodeFixInfo>();

        foreach (var fixProvider in providers)
        {
            var actions = new List<CodeAction>();

            var context = new CodeFixContext(
                document,
                diagnostic.Location.SourceSpan,
                ImmutableArray.Create(diagnostic),
                (action, _) => actions.Add(action),
                ct);

            await fixProvider.RegisterCodeFixesAsync(context);

            foreach (var action in actions)
            {
                fixes.Add(new CodeFixInfo(
                    Title: action.Title,
                    EquivalenceKey: action.EquivalenceKey ?? "",
                    DiagnosticId: diagnosticId,
                    ProviderName: fixProvider.GetType().Name));
            }
        }

        return fixes;
    }

    /// <summary>
    /// Applies a specific code fix and returns the modified content without writing to disk.
    /// </summary>
    public async Task<CodeFixResult?> ApplyFixAsync(
        ProjectSession session,
        string filePath,
        string diagnosticId,
        int line,
        int column,
        string equivalenceKey,
        CancellationToken ct = default,
        IAnalyzerProvider? analyzerProvider = null)
    {
        var provider = analyzerProvider ?? _registry;
        var providers = provider.GetCodeFixProvidersForDiagnostic(diagnosticId);
        if (providers.IsEmpty)
            return null;

        var document = session.GetDocument(filePath);
        if (document is null)
            return null;

        // Get original source text
        var originalText = await document.GetTextAsync(ct);
        var originalContent = originalText?.ToString() ?? "";

        // Find the diagnostic
        var diagnostic = await FindDiagnosticAsync(session, document, diagnosticId, line, column, ct, provider);
        if (diagnostic is null)
            return null;

        // Find the matching code action
        foreach (var fixProvider in providers)
        {
            var actions = new List<CodeAction>();

            var context = new CodeFixContext(
                document,
                diagnostic.Location.SourceSpan,
                ImmutableArray.Create(diagnostic),
                (action, _) => actions.Add(action),
                ct);

            await fixProvider.RegisterCodeFixesAsync(context);

            var matchingAction = actions.FirstOrDefault(a =>
                string.Equals(a.EquivalenceKey, equivalenceKey, StringComparison.Ordinal));

            if (matchingAction is null)
                continue;

            // Apply the code action to get the modified document
            var operations = await matchingAction.GetOperationsAsync(ct);

            foreach (var operation in operations)
            {
                if (operation is ApplyChangesOperation applyChanges)
                {
                    var changedSolution = applyChanges.ChangedSolution;
                    var changedDocument = changedSolution.GetDocument(document.Id);
                    if (changedDocument is null)
                        continue;

                    var newText = await changedDocument.GetTextAsync(ct);
                    var modifiedContent = newText?.ToString() ?? "";

                    return new CodeFixResult(
                        FilePath: filePath,
                        OriginalContent: originalContent,
                        ModifiedContent: modifiedContent,
                        FixTitle: matchingAction.Title);
                }
            }
        }

        return null;
    }

    private static async Task<Diagnostic?> FindDiagnosticAsync(
        ProjectSession session,
        Document document,
        string diagnosticId,
        int line,
        int column,
        CancellationToken ct,
        IAnalyzerProvider provider)
    {
        // Run analyzers to find diagnostics across the whole compilation
        // (some analyzers register via CompilationStartAction and don't produce per-file semantic diagnostics)
        var compilation = await session.GetCompilationAsync(ct);
        var analyzers = provider.GetAllAnalyzers()
            .Where(a => a.SupportedDiagnostics.Any(d => d.Id == diagnosticId))
            .ToImmutableArray();

        if (analyzers.IsEmpty)
            return null;

        var compilationWithAnalyzers = new CompilationWithAnalyzers(
            compilation, analyzers, null!, ct);

        var allDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        // Filter to the target file and diagnostic ID
        var documentPath = document.FilePath ?? "";
        var diagnostics = allDiagnostics
            .Where(d => d.Id == diagnosticId
                && d.Location.SourceTree?.FilePath is string fp
                && Path.GetFullPath(fp).Equals(Path.GetFullPath(documentPath), StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

        // Find the diagnostic at or near the specified line/column (1-based input)
        return diagnostics.FirstOrDefault(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            var startLine = lineSpan.StartLinePosition.Line + 1;
            var startCol = lineSpan.StartLinePosition.Character + 1;

            return startLine == line && startCol == column;
        })
        // Fallback: find any diagnostic with matching ID on the same line
        ?? diagnostics.FirstOrDefault(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            return lineSpan.StartLinePosition.Line + 1 == line;
        });
    }
}
