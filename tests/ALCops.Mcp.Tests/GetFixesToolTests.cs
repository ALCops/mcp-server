using System.Collections.Immutable;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// get_fixes: the success object and every <c>NotFound</c> reason. The reasons that the real
/// analyzer configuration cannot produce (NoAnalyzerForRule, NoFixForDiagnostic) are driven at the
/// <see cref="CodeFixRunner"/> level through an <see cref="IAnalyzerProvider"/> decorator.
/// </summary>
public sealed class GetFixesToolTests
{
    private static async Task<CallToolResult> GetFixesAsync(
        string projectPath, string fileName, string diagnosticId, int line, int column)
    {
        using var sessionManager = new ProjectSessionManager(new ProjectLoader());
        var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

        return await GetFixesTool.GetFixes(
            sessionManager, new CodeFixRunner(), analyzerResolver,
            projectPath, Path.Combine(projectPath, fileName), diagnosticId, line, column);
    }

    private static async Task<T> WithFixtureAsync<T>(string fixtureName, Func<string, Task<T>> body)
    {
        var projectPath = TestAnalyzers.CopyFixtureWithAnalyzers(fixtureName, "alcops-getfixes-test");
        try
        {
            return await body(projectPath);
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(projectPath);
        }
    }

    [Fact]
    public async Task Success_ReturnsObjectWithPositionAndFixes()
    {
        await WithFixtureAsync("ApplyFixProject", async projectPath =>
        {
            var result = await GetFixesAsync(projectPath, "MyPage.al", "LC0020", 11, 17);

            var root = ToolResultAssert.Ok(result);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);

            var fixes = ToolResultAssert.OkAs<GetFixesResult>(result);
            Assert.Equal("LC0020", fixes.DiagnosticId);
            Assert.Equal(Path.GetFullPath(Path.Combine(projectPath, "MyPage.al")), fixes.FilePath);
            Assert.Equal(11, fixes.Line);
            Assert.Equal(17, fixes.Column);
            Assert.NotEmpty(fixes.Fixes);
            Assert.All(fixes.Fixes, f =>
            {
                Assert.NotNull(f.EquivalenceKey);
                Assert.False(string.IsNullOrEmpty(f.Title));
                Assert.False(string.IsNullOrEmpty(f.ProviderName));
            });
            return 0;
        });
    }

    [Fact]
    public async Task NoFixProvider_ForUnknownRule()
    {
        await WithFixtureAsync("ApplyFixProject", async projectPath =>
        {
            var result = await GetFixesAsync(projectPath, "MyPage.al", "ZZ9999", 11, 17);

            var root = ToolResultAssert.Error(result, "NotFound", "NoFixProvider");
            Assert.Equal("ZZ9999", root.GetProperty("diagnosticId").GetString());
            return 0;
        });
    }

    [Fact]
    public async Task FileNotInProject_ForUnknownFile()
    {
        await WithFixtureAsync("ApplyFixProject", async projectPath =>
        {
            var result = await GetFixesAsync(projectPath, "Nope.al", "LC0020", 11, 17);

            ToolResultAssert.Error(result, "NotFound", "FileNotInProject");
            return 0;
        });
    }

    [Fact]
    public async Task NoDiagnosticAtPosition_WhenRuleIsNotReportedThere()
    {
        await WithFixtureAsync("ApplyFixProject", async projectPath =>
        {
            var result = await GetFixesAsync(projectPath, "MyPage.al", "LC0020", 1, 1);

            var root = ToolResultAssert.Error(result, "NotFound", "NoDiagnosticAtPosition");
            Assert.Contains("re-run analyze", root.GetProperty("message").GetString());
            Assert.False(root.TryGetProperty("candidates", out _));
            return 0;
        });
    }

    [Fact]
    public async Task SuppressedByRuleset_WhenRulesetSetsRuleToNone()
    {
        await WithFixtureAsync("FixAllRulesetProject", async projectPath =>
        {
            var result = await GetFixesAsync(projectPath, "PageA.al", "LC0020", 11, 17);

            ToolResultAssert.Error(result, "NotFound", "SuppressedByRuleset");
            return 0;
        });
    }

    [Fact]
    public async Task SuppressedByPragma_WhenPragmaDisablesRuleAtPosition()
    {
        await WithFixtureAsync("PragmaProject", async projectPath =>
        {
            // Control: the same page without the pragma has a fixable LC0020.
            var control = await GetFixesAsync(projectPath, "PageWithoutPragma.al", "LC0020", 11, 17);
            Assert.NotEmpty(ToolResultAssert.OkAs<GetFixesResult>(control).Fixes);

            // The pragma line shifts the field-level ApplicationArea down by one line.
            var result = await GetFixesAsync(projectPath, "PageWithPragma.al", "LC0020", 12, 17);

            ToolResultAssert.Error(result, "NotFound", "SuppressedByPragma");
            return 0;
        });
    }

    [Fact]
    public async Task Invalid_WhenProjectFolderHasNoAppJson()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"alcops-getfixes-noappjson-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var result = await GetFixesAsync(folder, "MyPage.al", "LC0020", 11, 17);
            ToolResultAssert.Error(result, "Invalid");

            var missing = await GetFixesAsync(Path.Combine(folder, "missing"), "MyPage.al", "LC0020", 11, 17);
            ToolResultAssert.Error(missing, "Invalid");
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task NoAnalyzerForRule_WhenFixProviderExistsButNoAnalyzerReportsTheRule()
    {
        await WithFixtureAsync("ApplyFixProject", async projectPath =>
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();
            var session = await sessionManager.GetOrLoadProjectAsync(projectPath);
            var real = await analyzerResolver.ResolveAsync(projectPath, null);

            var provider = new DecoratedProvider(real, hideAnalyzers: true);
            var lookup = await new CodeFixRunner().GetFixesAsync(
                session, Path.Combine(projectPath, "MyPage.al"), "LC0020", 11, 17, provider);

            Assert.Equal(FixNotFoundReason.NoAnalyzerForRule, lookup.NotFoundReason);
            return 0;
        });
    }

    [Fact]
    public async Task NoFixForDiagnostic_WhenProviderRegistersNothing()
    {
        await WithFixtureAsync("ApplyFixProject", async projectPath =>
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();
            var session = await sessionManager.GetOrLoadProjectAsync(projectPath);
            var real = await analyzerResolver.ResolveAsync(projectPath, null);

            var provider = new DecoratedProvider(real, fixProviders: [new SilentFixProvider()]);
            var runner = new CodeFixRunner();
            var filePath = Path.Combine(projectPath, "MyPage.al");

            var lookup = await runner.GetFixesAsync(session, filePath, "LC0020", 11, 17, provider);
            Assert.Equal(FixNotFoundReason.NoFixForDiagnostic, lookup.NotFoundReason);

            var apply = await runner.ApplyFixAsync(session, filePath, "LC0020", 11, 17, "", provider);
            Assert.Equal(FixNotFoundReason.NoFixForDiagnostic, apply.NotFoundReason);
            Assert.Null(apply.Fix);
            return 0;
        });
    }

    /// <summary>Wraps a real provider, optionally hiding its analyzers or replacing its fix providers.</summary>
    private sealed class DecoratedProvider(
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
    }

    /// <summary>Claims LC0020 but registers no code action.</summary>
    private sealed class SilentFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["LC0020"];

        public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
    }
}
