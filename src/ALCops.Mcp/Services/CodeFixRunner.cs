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
    /// <summary>
    /// Gets available code fixes for a specific diagnostic at a location, or the reason there are none.
    /// </summary>
    public async Task<FixLookupResult> GetFixesAsync(
        ProjectSession session,
        string filePath,
        string diagnosticId,
        int line,
        int column,
        IAnalyzerProvider analyzerProvider,
        CancellationToken ct = default)
    {
        var (located, notFound) = await LocateAsync(session, filePath, diagnosticId, line, column, analyzerProvider, ct);
        if (notFound is { } reason)
            return FixLookupResult.NotFound(reason);

        var (document, diagnostic, providers) = located!.Value;
        var pairs = await CollectActionsAsync(providers, document, diagnostic, ct);
        if (pairs.Count == 0)
            return FixLookupResult.NotFound(FixNotFoundReason.NoFixForDiagnostic);

        return FixLookupResult.Found([.. pairs.Select(p => ToInfo(p.Provider, p.Action))]);
    }

    /// <summary>
    /// Computes a code fix and returns the modified content without writing to disk, or the reason no
    /// fix applies. With <paramref name="equivalenceKey"/>, the first action with that key is applied
    /// (<see cref="FixNotFoundReason.NoFixForEquivalenceKey"/> with candidates when none has it).
    /// Without it, the single distinct fix is applied, or <see cref="FixApplyStatus.Ambiguous"/> is
    /// returned with the candidates when there are several.
    /// </summary>
    public async Task<FixApplyResult> ApplyFixAsync(
        ProjectSession session,
        string filePath,
        string diagnosticId,
        int line,
        int column,
        string? equivalenceKey,
        IAnalyzerProvider analyzerProvider,
        CancellationToken ct = default)
    {
        var (located, notFound) = await LocateAsync(session, filePath, diagnosticId, line, column, analyzerProvider, ct);
        if (notFound is { } reason)
            return FixApplyResult.NotFound(reason);

        var (document, diagnostic, providers) = located!.Value;
        var pairs = await CollectActionsAsync(providers, document, diagnostic, ct);
        if (pairs.Count == 0)
            return FixApplyResult.NotFound(FixNotFoundReason.NoFixForDiagnostic);

        var choice = ChooseFix(pairs, equivalenceKey);
        switch (choice.Kind)
        {
            case FixChoiceKind.NoFixForEquivalenceKey:
                return FixApplyResult.NotFound(FixNotFoundReason.NoFixForEquivalenceKey, choice.Candidates);
            case FixChoiceKind.Ambiguous:
                return FixApplyResult.Ambiguous(choice.Candidates);
        }

        var action = choice.Action!;
        var chosenKey = KeyOf(action);

        var operations = await action.GetOperationsAsync(ct);
        var applyChanges = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (applyChanges is not null)
        {
            var collected = await CollectChangesAsync(session, document.Project.Solution, applyChanges.ChangedSolution, ct);
            if (collected is null)
                return FixApplyResult.NotFound(FixNotFoundReason.UnsupportedFix);

            if (collected.Count > 0)
                return FixApplyResult.Applied(new CodeFixResult(action.Title, chosenKey, collected));
        }

        // The fix exists, but its action changes nothing. Same reason as "no fix at all", but get_fixes
        // did advertise this key, so the message says what actually happened.
        return FixApplyResult.NotFound(FixNotFoundReason.NoFixForDiagnostic) with
        {
            MessageOverride =
                $"A fix with equivalence key '{chosenKey}' exists for {diagnosticId} at {filePath}:{line}:{column} " +
                "but produced no change."
        };
    }

    /// <summary>
    /// Every document <paramref name="changed"/> changes relative to <paramref name="original"/>, with
    /// its old and new text, ordered by file path (ordinal, case-insensitive). The only place the
    /// documents a fix changes are discovered. Returns <c>null</c> when the fix cannot be applied as
    /// file writes: it adds or removes a project or document, or changes a document without a file
    /// path or one the session does not track (<see cref="FixNotFoundReason.UnsupportedFix"/>).
    /// A document whose text ends up identical is not a change.
    /// </summary>
    internal static async Task<IReadOnlyList<DocumentChange>?> CollectChangesAsync(
        ProjectSession session, Solution original, Solution changed, CancellationToken ct)
    {
        var solutionChanges = changed.GetChanges(original);
        if (solutionChanges.GetAddedProjects().Any() || solutionChanges.GetRemovedProjects().Any())
            return null;

        var changes = new List<DocumentChange>();
        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            if (projectChanges.GetAddedDocuments().Any() || projectChanges.GetRemovedDocuments().Any())
                return null;

            foreach (var documentId in projectChanges.GetChangedDocuments())
            {
                var before = original.GetDocument(documentId);
                var after = changed.GetDocument(documentId);
                if (before is null || after is null
                    || before.FilePath is not string path
                    || session.GetDocument(path) is null)
                    return null;

                var oldText = await before.GetTextAsync(ct);
                var newText = await after.GetTextAsync(ct);
                if (oldText.ContentEquals(newText))
                    continue;

                changes.Add(new DocumentChange(Path.GetFullPath(path), oldText.ToString(), newText.ToString()));
            }
        }

        return [.. changes.OrderBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase)];
    }

    private enum FixChoiceKind { Chosen, NoFixForEquivalenceKey, Ambiguous }

    /// <summary>
    /// The fix <see cref="ChooseFix"/> picked (<see cref="Provider"/>/<see cref="Action"/>), or why it
    /// picked none; <see cref="Candidates"/> is filled for the two failure kinds.
    /// </summary>
    private readonly record struct FixChoice(
        FixChoiceKind Kind, CodeFixProvider? Provider, CodeAction? Action, IReadOnlyList<CodeFixInfo> Candidates);

    /// <summary>
    /// The one place apply_fix and apply_fix_all decide which fix to apply. With
    /// <paramref name="requestedKey"/>, the first action with that key (as advertised by
    /// <see cref="KeyOf"/>), or <see cref="FixChoiceKind.NoFixForEquivalenceKey"/>. Without it, the first
    /// action when every action shares one key, or <see cref="FixChoiceKind.Ambiguous"/>. Candidates are
    /// <see cref="ToCandidates"/>, so the first occurrence per key is both advertised and applied.
    /// <paramref name="candidates"/> must not be empty.
    /// </summary>
    private static FixChoice ChooseFix(
        IReadOnlyList<(CodeFixProvider Provider, CodeAction Action)> candidates, string? requestedKey)
    {
        if (requestedKey is not null)
        {
            foreach (var (provider, action) in candidates)
            {
                if (KeyOf(action) == requestedKey)
                    return new FixChoice(FixChoiceKind.Chosen, provider, action, []);
            }

            return new FixChoice(FixChoiceKind.NoFixForEquivalenceKey, null, null, ToCandidates(candidates));
        }

        var distinct = ToCandidates(candidates);
        if (distinct.Count > 1)
            return new FixChoice(FixChoiceKind.Ambiguous, null, null, distinct);

        return new FixChoice(FixChoiceKind.Chosen, candidates[0].Provider, candidates[0].Action, []);
    }

    /// <summary>
    /// The equivalence key a caller sees and passes back. A null key is advertised as <c>""</c>, so
    /// every comparison goes through this too; otherwise a null-key action could never be selected.
    /// </summary>
    private static string KeyOf(CodeAction action) => action.EquivalenceKey ?? "";

    private static CodeFixInfo ToInfo(CodeFixProvider provider, CodeAction action) =>
        new(KeyOf(action), action.Title, provider.GetType().Name);

    /// <summary>
    /// One candidate per distinct equivalence key, in registration order. When two providers (or two
    /// actions) share a key, the first occurrence wins: that is also the one a key match selects.
    /// </summary>
    private static IReadOnlyList<CodeFixInfo> ToCandidates(IEnumerable<(CodeFixProvider Provider, CodeAction Action)> pairs) =>
        [.. pairs
            .GroupBy(p => KeyOf(p.Action), StringComparer.Ordinal)
            .Select(g => ToInfo(g.First().Provider, g.First().Action))];

    /// <summary>True when the project ruleset sets <paramref name="diagnosticId"/> to <c>None</c>.</summary>
    private static bool IsRulesetSuppressed(IAnalyzerProvider provider, string diagnosticId) =>
        provider is AnalyzerSet { RuleActions: var ruleActions } && RulesetFilter.IsSuppressed(ruleActions, diagnosticId, out _);

    /// <summary>
    /// The shared front half of get_fixes and apply_fix: a fix provider, the document, no ruleset
    /// suppression, and the diagnostic at the position, in that order. Returns the first failing reason.
    /// </summary>
    private static async Task<((Document Document, Diagnostic Diagnostic, ImmutableArray<CodeFixProvider> Providers)? Located, FixNotFoundReason? NotFound)> LocateAsync(
        ProjectSession session,
        string filePath,
        string diagnosticId,
        int line,
        int column,
        IAnalyzerProvider analyzerProvider,
        CancellationToken ct)
    {
        var providers = analyzerProvider.GetCodeFixProvidersForDiagnostic(diagnosticId);
        if (providers.IsEmpty)
            return (null, FixNotFoundReason.NoFixProvider);

        var document = session.GetDocument(filePath);
        if (document is null)
            return (null, FixNotFoundReason.FileNotInProject);

        if (IsRulesetSuppressed(analyzerProvider, diagnosticId))
            return (null, FixNotFoundReason.SuppressedByRuleset);

        var lookup = await FindDiagnosticAsync(session, document, diagnosticId, line, column, ct, analyzerProvider);
        if (lookup.Reason is { } reason)
            return (null, reason);

        return ((document, lookup.Diagnostic!, providers), null);
    }

    /// <summary>Every code action every provider registers for <paramref name="diagnostic"/>, in provider order.</summary>
    private static async Task<List<(CodeFixProvider Provider, CodeAction Action)>> CollectActionsAsync(
        ImmutableArray<CodeFixProvider> providers,
        Document document,
        Diagnostic diagnostic,
        CancellationToken ct)
    {
        var pairs = new List<(CodeFixProvider Provider, CodeAction Action)>();

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
                pairs.Add((fixProvider, action));
        }

        return pairs;
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
        IAnalyzerProvider analyzerProvider,
        CancellationToken ct = default)
    {
        var providers = analyzerProvider.GetCodeFixProvidersForDiagnostic(diagnosticId);
        if (providers.IsEmpty)
            return NotFound(diagnosticId, FixNotFoundReason.NoFixProvider);

        // Checked up front so a suppressed rule is reported as such rather than as zero occurrences.
        if (IsRulesetSuppressed(analyzerProvider, diagnosticId))
            return NotFound(diagnosticId, FixNotFoundReason.SuppressedByRuleset);

        // Same for a rule no loaded analyzer reports: get_fixes and apply_fix say NoAnalyzerForRule, and
        // "zero occurrences" would wrongly tell the caller the code is clean.
        if (!analyzerProvider.GetAllAnalyzers().Any(a => a.SupportedDiagnostics.Any(d => d.Id == diagnosticId)))
            return NotFound(diagnosticId, FixNotFoundReason.NoAnalyzerForRule);

        var normalizedFilePath = filePath is null ? null : Path.GetFullPath(filePath);

        if (scope == FixAllScope.Document && session.GetDocument(normalizedFilePath!) is null)
            return NotFound(diagnosticId, FixNotFoundReason.FileNotInProject);

        var diagnostics = await CollectDiagnosticsForRuleAsync(session, diagnosticId, normalizedFilePath, ct, analyzerProvider);
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
            return NotFound(diagnosticId, FixNotFoundReason.NoFixForDiagnostic, diagnostics.Length);

        var choice = ChooseFix(candidateActions, equivalenceKey);
        switch (choice.Kind)
        {
            case FixChoiceKind.NoFixForEquivalenceKey:
                return NotFound(diagnosticId, FixNotFoundReason.NoFixForEquivalenceKey, diagnostics.Length,
                    choice.Candidates);
            case FixChoiceKind.Ambiguous:
                return new FixAllResult(
                    FixAllStatus.Ambiguous, diagnosticId, diagnostics.Length,
                    null, null, [], choice.Candidates, []);
        }

        var fixProviderInstance = choice.Provider!;
        var chosenKey = KeyOf(choice.Action!);
        var fixTitle = choice.Action!.Title;

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

                var documentId = session.GetDocument(group.Key)?.Id;
                if (documentId is null || changedSolution.GetDocument(documentId) is null)
                    continue;

                changedSolution = await ApplyIterativeFixesAsync(
                    changedSolution, documentId, [.. group.OrderByDescending(d => d.Location.SourceSpan.Start)],
                    fixProviderInstance, chosenKey, ct);
            }
        }

        // Every file the pass changed, including side edits to files without a diagnostic.
        var collected = await CollectChangesAsync(session, project.Solution, changedSolution, ct);
        if (collected is null)
            return NotFound(diagnosticId, FixNotFoundReason.UnsupportedFix, diagnostics.Length);

        var diagnosticsByFile = diagnostics
            .Where(d => d.Location.SourceTree?.FilePath is not null)
            .GroupBy(d => Path.GetFullPath(d.Location.SourceTree!.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FixAllUnfixedDiagnostic>)[.. g.Select(ToLocation)], StringComparer.OrdinalIgnoreCase);

        var changes = collected
            .Select(c => new FixAllFileChange(
                c.FilePath, c.OriginalContent, c.ModifiedContent,
                diagnosticsByFile.TryGetValue(c.FilePath, out var fileDiagnostics) ? fileDiagnostics : []))
            .ToList();

        // Re-check the changed solution for any remaining occurrences of the rule so
        // callers know exactly what, if anything, the fix-all pass could not resolve.
        var unfixed = await FindRemainingDiagnosticsAsync(
            changedSolution, project.Id, diagnosticId, normalizedFilePath, ct, analyzerProvider);

        return new FixAllResult(
            FixAllStatus.Completed, diagnosticId, diagnostics.Length, fixTitle, chosenKey,
            changes, [], unfixed);
    }

    private static FixAllResult NotFound(
        string diagnosticId, FixNotFoundReason reason, int diagnosticsFound = 0, IReadOnlyList<CodeFixInfo>? candidates = null) =>
        new(FixAllStatus.NotFound, diagnosticId, diagnosticsFound, null, null, [], candidates ?? [], [], reason);

    /// <summary>
    /// Applies fixes to a single document one diagnostic at a time, in descending source-position
    /// order, so each successive fix operates on text that hasn't shifted above the next target span.
    /// Best-effort: a diagnostic whose action can't be located or applied is simply left in place —
    /// the caller's remaining-diagnostics recheck reports it as unfixed rather than dropping it silently.
    /// The whole <see cref="Solution"/> is threaded through, so an action's edits to other documents
    /// survive. Such a cross-document side edit into a file whose own diagnostics are still pending is
    /// best-effort as well: it may shift their spans, and the recheck then reports what was missed.
    /// </summary>
    private static async Task<Solution> ApplyIterativeFixesAsync(
        Solution solution,
        DocumentId documentId,
        IReadOnlyList<Diagnostic> orderedDiagnostics,
        CodeFixProvider fixProvider,
        string equivalenceKey,
        CancellationToken ct)
    {
        var changedSolution = solution;
        var current = solution.GetDocument(documentId)!;
        foreach (var diagnostic in orderedDiagnostics)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(
                current, diagnostic.Location.SourceSpan, ImmutableArray.Create(diagnostic),
                (action, _) => actions.Add(action), ct);

            await fixProvider.RegisterCodeFixesAsync(context);

            var match = actions.FirstOrDefault(a => KeyOf(a) == equivalenceKey);
            if (match is null)
                continue;

            var operations = await match.GetOperationsAsync(ct);
            foreach (var operation in operations)
            {
                if (operation is not ApplyChangesOperation applyChanges)
                    continue;

                var changedDoc = applyChanges.ChangedSolution.GetDocument(current.Id);
                if (changedDoc is null)
                    continue;

                changedSolution = applyChanges.ChangedSolution;
                current = changedDoc;
            }
        }

        return changedSolution;
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

        return [.. remaining.Select(ToLocation)];
    }

    private static FixAllUnfixedDiagnostic ToLocation(Diagnostic d)
    {
        var lineSpan = d.Location.GetLineSpan();
        return new FixAllUnfixedDiagnostic(
            d.Location.SourceTree?.FilePath ?? "",
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
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

    /// <summary>
    /// Finds the diagnostic at a position, or says why there is none. Ruleset suppression is checked by
    /// the caller before analysis; pragma suppression is detected here, which relies on
    /// <c>GetEffectiveDiagnostics</c> keeping pragma-suppressed diagnostics with <c>IsSuppressed = true</c>
    /// rather than dropping them.
    /// </summary>
    private static async Task<DiagnosticLookup> FindDiagnosticAsync(
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
            return new DiagnosticLookup(null, FixNotFoundReason.NoAnalyzerForRule);

        var compilationWithAnalyzers = new CompilationWithAnalyzers(
            compilation, analyzers, null!, ct);

        var rawDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        // Apply pragma suppression filtering
        var effectiveDiagnostics = CompilationWithAnalyzers
            .GetEffectiveDiagnostics(rawDiagnostics, compilation);

        // Filter to the target file and diagnostic ID. Suppressed diagnostics are kept here so a
        // pragma-suppressed hit can be told apart from no hit at all.
        var documentPath = document.FilePath ?? "";
        var diagnostics = effectiveDiagnostics
            .Where(d => d.Id == diagnosticId
                && d.Location.SourceTree?.FilePath is string fp
                && Path.GetFullPath(fp).Equals(Path.GetFullPath(documentPath), StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

        var (hit, suppressed) = MatchPosition(diagnostics, StartOf, d => d.IsSuppressed, line, column);
        if (hit is null)
            return new DiagnosticLookup(null, FixNotFoundReason.NoDiagnosticAtPosition);

        return suppressed
            ? new DiagnosticLookup(null, FixNotFoundReason.SuppressedByPragma)
            : new DiagnosticLookup(hit, null);
    }

    /// <summary>1-based start line/column of a diagnostic.</summary>
    private static (int Line, int Column) StartOf(Diagnostic d)
    {
        var start = d.Location.GetLineSpan().StartLinePosition;
        return (start.Line + 1, start.Character + 1);
    }

    /// <summary>
    /// Picks the candidate at the 1-based <paramref name="line"/>/<paramref name="column"/>. An exact
    /// match wins over a same-line fallback in either set, so a pragma-suppressed diagnostic at the exact
    /// position is not answered with a live neighbour on the same line. Order: exact live, exact
    /// suppressed, same-line live, same-line suppressed. <c>Suppressed</c> says which set the hit came from.
    /// </summary>
    internal static (T? Hit, bool Suppressed) MatchPosition<T>(
        IEnumerable<T> candidates,
        Func<T, (int Line, int Column)> startOf,
        Func<T, bool> isSuppressed,
        int line,
        int column)
        where T : class
    {
        var all = candidates.ToList();
        var live = all.Where(d => !isSuppressed(d)).ToList();
        var suppressed = all.Where(isSuppressed).ToList();

        if (ExactlyAt(live, startOf, line, column) is { } exactLive)
            return (exactLive, false);
        if (ExactlyAt(suppressed, startOf, line, column) is { } exactSuppressed)
            return (exactSuppressed, true);
        if (OnLine(live, startOf, line) is { } lineLive)
            return (lineLive, false);
        if (OnLine(suppressed, startOf, line) is { } lineSuppressed)
            return (lineSuppressed, true);
        return (null, false);
    }

    /// <summary>The first candidate starting exactly at <paramref name="line"/>/<paramref name="column"/>.</summary>
    private static T? ExactlyAt<T>(IEnumerable<T> candidates, Func<T, (int Line, int Column)> startOf, int line, int column)
        where T : class =>
        candidates.FirstOrDefault(d => startOf(d) == (line, column));

    /// <summary>The first candidate starting anywhere on <paramref name="line"/>.</summary>
    private static T? OnLine<T>(IEnumerable<T> candidates, Func<T, (int Line, int Column)> startOf, int line)
        where T : class =>
        candidates.FirstOrDefault(d => startOf(d).Line == line);
}
