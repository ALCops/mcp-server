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

    /// <summary>
    /// Applies a code fix to every occurrence of a diagnostic rule across a project (or a single
    /// document, when <paramref name="scope"/> is <see cref="FixAllScope.Document"/>). Runs analysis
    /// once, then prefers each provider's registered <see cref="FixAllProvider"/> (e.g. ALCops' shared
    /// BatchFixer), falling back to an iterative per-document apply when a provider offers none.
    /// Does not write to disk or touch the session's workspace — callers apply <see cref="FixAllResult.Changes"/>.
    /// </summary>
    public async Task<FixAllResult> ApplyFixAllAsync(
        ProjectSession session,
        string diagnosticId,
        FixAllScope scope,
        string? filePath,
        string? equivalenceKey,
        CancellationToken ct = default,
        IAnalyzerProvider? analyzerProvider = null)
    {
        var provider = analyzerProvider ?? _registry;
        var providers = provider.GetCodeFixProvidersForDiagnostic(diagnosticId);
        if (providers.IsEmpty)
            return NoFixAvailable(diagnosticId);

        var normalizedFilePath = filePath is null ? null : Path.GetFullPath(filePath);

        var diagnostics = await CollectDiagnosticsForRuleAsync(session, diagnosticId, normalizedFilePath, ct, provider);
        if (diagnostics.IsEmpty)
            return new FixAllResult(FixAllStatus.NoDiagnosticsFound, diagnosticId, 0, null, null, [], [], []);

        // Resolve which provider + equivalence key to fix with, using the first diagnostic as a probe.
        var probeDiagnostic = diagnostics[0];
        var candidateActions = new List<(CodeFixProvider Provider, CodeAction Action)>();
        foreach (var fixProvider in providers)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(
                session.GetDocument(probeDiagnostic.Location.SourceTree!.FilePath!)!,
                probeDiagnostic.Location.SourceSpan,
                ImmutableArray.Create(probeDiagnostic),
                (action, _) => actions.Add(action),
                ct);
            await fixProvider.RegisterCodeFixesAsync(context);
            foreach (var action in actions)
                candidateActions.Add((fixProvider, action));
        }

        if (candidateActions.Count == 0)
            return NoFixAvailable(diagnosticId, diagnostics.Length);

        (CodeFixProvider Provider, CodeAction Action) chosen;
        if (equivalenceKey is not null)
        {
            var match = candidateActions.FirstOrDefault(c =>
                string.Equals(c.Action.EquivalenceKey, equivalenceKey, StringComparison.Ordinal));
            if (match.Action is null)
                return NoFixAvailable(diagnosticId, diagnostics.Length);
            chosen = match;
        }
        else
        {
            var distinctKeys = candidateActions
                .Select(c => c.Action.EquivalenceKey ?? "")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinctKeys.Count > 1)
                return new FixAllResult(
                    FixAllStatus.AmbiguousFix, diagnosticId, diagnostics.Length,
                    null, null, [], distinctKeys, []);

            chosen = candidateActions[0];
        }

        var fixProviderInstance = chosen.Provider;
        var chosenKey = chosen.Action.EquivalenceKey ?? "";
        var fixTitle = chosen.Action.Title;

        var project = session.GetProject();
        var diagnosticProvider = new PrecomputedDiagnosticProvider(diagnostics);

        Solution? changedSolution = null;
        var fixAllProvider = fixProviderInstance.GetFixAllProvider();

        if (fixAllProvider is not null)
        {
            var context = scope == FixAllScope.Document
                ? new FixAllContext(
                    session.GetDocument(normalizedFilePath!)!, fixProviderInstance, scope, chosenKey,
                    [diagnosticId], diagnosticProvider, ct)
                : new FixAllContext(
                    project, fixProviderInstance, scope, chosenKey,
                    [diagnosticId], diagnosticProvider, ct);

            var fixAllAction = await fixAllProvider.GetFixAsync(context);
            if (fixAllAction is not null)
            {
                var operations = await fixAllAction.GetOperationsAsync(ct);
                foreach (var operation in operations)
                {
                    if (operation is ApplyChangesOperation applyChanges)
                        changedSolution = applyChanges.ChangedSolution;
                }
            }
        }

        // Fallback: no FixAllProvider registered, or it declined to produce an action.
        // Apply fixes one-by-one per document, bottom-up, so earlier text edits never
        // invalidate the character offsets of diagnostics still pending above them.
        if (changedSolution is null)
        {
            changedSolution = project.Solution;
            foreach (var group in diagnostics.GroupBy(d => d.Location.SourceTree?.FilePath))
            {
                if (group.Key is null)
                    continue;

                var document = changedSolution.GetDocument(session.GetDocument(group.Key)?.Id);
                if (document is null)
                    continue;

                var finalDocument = await ApplyIterativeFixesAsync(
                    document, [.. group.OrderByDescending(d => d.Location.SourceSpan.Start)],
                    fixProviderInstance, chosenKey, ct);

                changedSolution = changedSolution.WithDocumentText(finalDocument.Id, await finalDocument.GetTextAsync(ct));
            }
        }

        // Diff against the original text to find which files actually changed.
        var changes = new List<FixAllFileChange>();
        foreach (var group in diagnostics.GroupBy(d => d.Location.SourceTree?.FilePath))
        {
            if (group.Key is null)
                continue;

            var originalDocument = session.GetDocument(group.Key);
            if (originalDocument is null)
                continue;

            var changedDocument = changedSolution.GetDocument(originalDocument.Id);
            if (changedDocument is null)
                continue;

            var originalText = (await originalDocument.GetTextAsync(ct)).ToString();
            var newText = (await changedDocument.GetTextAsync(ct)).ToString();
            if (!string.Equals(originalText, newText, StringComparison.Ordinal))
                changes.Add(new FixAllFileChange(group.Key, newText));
        }

        // Re-check the changed solution for any remaining occurrences of the rule so
        // callers know exactly what, if anything, the fix-all pass could not resolve.
        var unfixed = await FindRemainingDiagnosticsAsync(
            changedSolution, project.Id, diagnosticId, normalizedFilePath, ct, provider);

        return new FixAllResult(
            FixAllStatus.Completed, diagnosticId, diagnostics.Length, fixTitle, chosenKey,
            changes, [], unfixed);
    }

    private static FixAllResult NoFixAvailable(string diagnosticId, int diagnosticsFound = 0) =>
        new(FixAllStatus.NoFixAvailable, diagnosticId, diagnosticsFound, null, null, [], [], []);

    /// <summary>
    /// Applies fixes to a single document one diagnostic at a time, in descending source-position
    /// order, so each successive fix operates on text that hasn't shifted above the next target span.
    /// Best-effort: a diagnostic whose action can't be located or applied is simply left in place —
    /// the caller's remaining-diagnostics recheck reports it as unfixed rather than dropping it silently.
    /// </summary>
    private static async Task<Document> ApplyIterativeFixesAsync(
        Document document,
        IReadOnlyList<Diagnostic> orderedDiagnostics,
        CodeFixProvider fixProvider,
        string equivalenceKey,
        CancellationToken ct)
    {
        var current = document;
        foreach (var diagnostic in orderedDiagnostics)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(
                current, diagnostic.Location.SourceSpan, ImmutableArray.Create(diagnostic),
                (action, _) => actions.Add(action), ct);

            await fixProvider.RegisterCodeFixesAsync(context);

            var match = actions.FirstOrDefault(a =>
                string.Equals(a.EquivalenceKey, equivalenceKey, StringComparison.Ordinal));
            if (match is null)
                continue;

            var operations = await match.GetOperationsAsync(ct);
            foreach (var operation in operations)
            {
                if (operation is not ApplyChangesOperation applyChanges)
                    continue;

                var changedDoc = applyChanges.ChangedSolution.GetDocument(current.Id);
                if (changedDoc is not null)
                    current = changedDoc;
            }
        }

        return current;
    }

    /// <summary>
    /// Collects effective (pragma-suppression-aware) diagnostics for one rule across the project,
    /// optionally restricted to a single file.
    /// </summary>
    private static async Task<ImmutableArray<Diagnostic>> CollectDiagnosticsForRuleAsync(
        ProjectSession session,
        string diagnosticId,
        string? filePath,
        CancellationToken ct,
        IAnalyzerProvider provider)
    {
        var compilation = await session.GetCompilationAsync(ct);
        var analyzers = provider.GetAllAnalyzers()
            .Where(a => a.SupportedDiagnostics.Any(d => d.Id == diagnosticId))
            .ToImmutableArray();

        if (analyzers.IsEmpty)
            return [];

        var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, null!, ct);
        var rawDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        var effectiveDiagnostics = CompilationWithAnalyzers.GetEffectiveDiagnostics(rawDiagnostics, compilation);
        var ruleActions = provider is AnalyzerSet analyzerSet ? analyzerSet.RuleActions : null;

        return [.. effectiveDiagnostics
            .Where(d => !d.IsSuppressed && d.Id == diagnosticId && !RulesetFilter.IsSuppressed(ruleActions, d.Id, out _))
            .Where(d => filePath is null
                || (d.Location.SourceTree?.FilePath is string fp
                    && Path.GetFullPath(fp).Equals(filePath, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// Re-runs the rule's analyzers against a (possibly already-fixed) solution to report any
    /// remaining occurrences after a fix-all pass.
    /// </summary>
    private static async Task<IReadOnlyList<FixAllUnfixedDiagnostic>> FindRemainingDiagnosticsAsync(
        Solution solution,
        ProjectId projectId,
        string diagnosticId,
        string? filePath,
        CancellationToken ct,
        IAnalyzerProvider provider)
    {
        var project = solution.GetProject(projectId);
        if (project is null)
            return [];

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return [];

        var analyzers = provider.GetAllAnalyzers()
            .Where(a => a.SupportedDiagnostics.Any(d => d.Id == diagnosticId))
            .ToImmutableArray();

        if (analyzers.IsEmpty)
            return [];

        var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, null!, ct);
        var rawDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        var effectiveDiagnostics = CompilationWithAnalyzers.GetEffectiveDiagnostics(rawDiagnostics, compilation);
        var ruleActions = provider is AnalyzerSet analyzerSet ? analyzerSet.RuleActions : null;

        var remaining = effectiveDiagnostics
            .Where(d => !d.IsSuppressed && d.Id == diagnosticId && !RulesetFilter.IsSuppressed(ruleActions, d.Id, out _))
            .Where(d => filePath is null
                || (d.Location.SourceTree?.FilePath is string fp
                    && Path.GetFullPath(fp).Equals(filePath, StringComparison.OrdinalIgnoreCase)));

        return [.. remaining.Select(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            return new FixAllUnfixedDiagnostic(
                d.Location.SourceTree?.FilePath ?? "",
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1);
        })];
    }

    /// <summary>
    /// Serves pre-computed diagnostics to a <see cref="FixAllContext"/> so the SDK's
    /// <see cref="FixAllProvider"/> implementations don't need to re-run analysis.
    /// </summary>
    private sealed class PrecomputedDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        private readonly ImmutableArray<Diagnostic> _all;
        private readonly ILookup<string, Diagnostic> _byFilePath;

        public PrecomputedDiagnosticProvider(ImmutableArray<Diagnostic> diagnostics)
        {
            _all = diagnostics;
            _byFilePath = diagnostics
                .Where(d => d.Location.SourceTree?.FilePath is not null)
                .ToLookup(d => Path.GetFullPath(d.Location.SourceTree!.FilePath!), StringComparer.OrdinalIgnoreCase);
        }

        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(
            Document document, ISet<string> diagnosticIds, CancellationToken ct)
        {
            if (document.FilePath is null)
                return Task.FromResult(Enumerable.Empty<Diagnostic>());

            var normalized = Path.GetFullPath(document.FilePath);
            return Task.FromResult(_byFilePath[normalized].Where(d => diagnosticIds.Contains(d.Id)));
        }

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(
            Project project, ISet<string> diagnosticIds, CancellationToken ct) =>
            Task.FromResult(_all.Where(d => d.Location.SourceTree is null && diagnosticIds.Contains(d.Id)));

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
            Project project, ISet<string> diagnosticIds, CancellationToken ct) =>
            Task.FromResult(_all.Where(d => diagnosticIds.Contains(d.Id)));
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

        var rawDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        // Apply pragma suppression filtering (same as DiagnosticsRunner)
        var effectiveDiagnostics = CompilationWithAnalyzers
            .GetEffectiveDiagnostics(rawDiagnostics, compilation);

        // Apply ruleset suppression (RuleAction.None), same as DiagnosticsRunner
        var ruleActions = provider is AnalyzerSet analyzerSet ? analyzerSet.RuleActions : null;

        // Filter to the target file and diagnostic ID
        var documentPath = document.FilePath ?? "";
        var diagnostics = effectiveDiagnostics
            .Where(d => !d.IsSuppressed && !RulesetFilter.IsSuppressed(ruleActions, d.Id, out _))
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
