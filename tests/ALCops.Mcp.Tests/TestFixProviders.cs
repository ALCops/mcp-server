using System.Collections.Immutable;
using ALCops.Mcp.Services;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeActions;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Text;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;

namespace ALCops.Mcp.Tests;

/// <summary>Wraps a real provider, optionally hiding its analyzers or replacing its fix providers.</summary>
internal sealed class DecoratedProvider(
    IAnalyzerProvider inner,
    bool hideAnalyzers = false,
    ImmutableArray<CodeFixProvider>? fixProviders = null) : IAnalyzerProvider
{
    public ImmutableArray<DiagnosticAnalyzer> GetAllAnalyzers() => hideAnalyzers ? [] : inner.GetAllAnalyzers();
    public ImmutableArray<CodeFixProvider> GetAllCodeFixProviders() => fixProviders ?? inner.GetAllCodeFixProviders();
    public ImmutableArray<CodeFixProvider> GetCodeFixProvidersForDiagnostic(string diagnosticId) =>
        fixProviders ?? inner.GetCodeFixProvidersForDiagnostic(diagnosticId);
    public ImmutableDictionary<string, DiagnosticDescriptor> GetAllDescriptors() => inner.GetAllDescriptors();
    public string GetCopName(string diagnosticId) => inner.GetCopName(diagnosticId);
    public bool HasCodeFix(string diagnosticId) => inner.HasCodeFix(diagnosticId);

    /// <summary>
    /// The analyzer-resolution delegate the tools' <c>RunAsync</c> seam takes: resolves the project's
    /// real analyzers, then replaces their fix providers with <paramref name="fixProviders"/>.
    /// </summary>
    public static Func<string, IReadOnlyList<string>?, CancellationToken, Task<IAnalyzerProvider>> Resolving(
        ProjectAnalyzerResolver resolver, params CodeFixProvider[] fixProviders) =>
        async (path, specs, ct) => new DecoratedProvider(
            await resolver.ResolveAsync(path, specs, ct), fixProviders: [.. fixProviders]);
}

/// <summary>
/// A test-only LC0020 fix (no ALCops provider edits a second document): removes the line holding the
/// redundant <c>ApplicationArea = All;</c> and appends a comment to a second document,
/// <paramref name="secondFileName"/>. Registers no <see cref="FixAllProvider"/>, so apply_fix_all
/// takes the iterative fallback.
/// </summary>
internal sealed class TwoDocumentFixProvider(string secondFileName) : CodeFixProvider
{
    public const string Key = "two-docs";
    public const string Comment = "// touched by two-docs";

    public override ImmutableArray<string> FixableDiagnosticIds => ["LC0020"];

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var document = context.Document;
        var span = context.Span;
        context.RegisterCodeFix(
            CodeAction.Create("Remove and touch a second file", async ct =>
            {
                var text = await document.GetTextAsync(ct);
                var lineSpan = text.Lines.GetLineFromPosition(span.Start).SpanIncludingLineBreak;
                var solution = document.Project.Solution
                    .WithDocumentText(document.Id, text.WithChanges(new TextChange(lineSpan, "")));

                var second = solution.GetProject(document.Project.Id)!.Documents
                    .Single(d => string.Equals(Path.GetFileName(d.FilePath), secondFileName, StringComparison.OrdinalIgnoreCase));
                var secondText = (await second.GetTextAsync(ct)).ToString();
                return solution.WithDocumentText(second.Id, SourceText.From(secondText + Comment + "\n"));
            }, Key),
            context.Diagnostics[0]);
        return Task.CompletedTask;
    }
}

/// <summary>A test-only LC0020 fix that adds a new document, which the server cannot apply.</summary>
internal sealed class AddDocumentFixProvider : CodeFixProvider
{
    public const string NewFileName = "Added.al";

    public override ImmutableArray<string> FixableDiagnosticIds => ["LC0020"];

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var document = context.Document;
        context.RegisterCodeFix(
            CodeAction.Create("Add a file", ct =>
            {
                var path = Path.Combine(Path.GetDirectoryName(document.FilePath)!, NewFileName);
                var solution = document.Project.Solution.AddDocument(
                    DocumentId.CreateNewId(document.Project.Id), NewFileName, "codeunit 50199 Added { }", filePath: path);
                return Task.FromResult(solution);
            }, "add-doc"),
            context.Diagnostics[0]);
        return Task.CompletedTask;
    }
}
