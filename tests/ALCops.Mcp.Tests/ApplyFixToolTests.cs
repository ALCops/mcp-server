using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Regression test for the mcp-server README claim (previously stale, see issue #15) that
/// apply_fix writes the modified content to disk. This locks in the actual behavior of
/// ApplyFixTool so documentation cannot silently drift from the implementation again.
/// </summary>
public class ApplyFixToolTests
{
    private static (ProjectAnalyzerResolver Resolver, ExternalAnalyzerLoader Loader) CreateAnalyzerResolver()
    {
        var loader = new ExternalAnalyzerLoader(TestAnalyzers.ToolsLocator);
        return (new ProjectAnalyzerResolver(loader, new RulesetLoader()), loader);
    }

    [Fact]
    public async Task ApplyFix_WritesModifiedContentToDisk()
    {
        // Copy the fixture to a temp directory so this test can safely mutate the file
        // without affecting the checked-in fixture used by other tests/runs.
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("ApplyFixProject", "alcops-applyfix-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var originalContent = await File.ReadAllTextAsync(filePath);

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var codeFixRunner = new CodeFixRunner();
            var (analyzerResolver, _) = CreateAnalyzerResolver();

            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);

            // LC0020 (ApplicationAreaRedundancy) on the field-level ApplicationArea in MyPage.al.
            // Hardcoded rather than discovered, matching GetFixes_RulesetSuppressesRule_ReturnsNoFixes.
            const int line = 11, column = 17;
            var fixes = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, analyzerSet);

            Assert.True(fixes.Count > 0,
                $"Expected a fixable LC0020 at line {line}, column {column}. Loaded {analyzerSet.GetAllAnalyzers().Length} " +
                $"analyzer(s); warnings: {string.Join("; ", analyzerSet.Warnings)}. " +
                "The fixture, the location, or the analyzer configuration may have changed.");

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, codeFixRunner, analyzerResolver,
                tempProjectPath, filePath, "LC0020", line, column,
                fixes[0].EquivalenceKey);

            Assert.Contains("\"applied\":true", result);

            // The actual regression assertion: the file on disk must have changed.
            var updatedContent = await File.ReadAllTextAsync(filePath);
            Assert.NotEqual(originalContent, updatedContent);
        }
        finally
        {
            if (Directory.Exists(tempProjectPath))
                Directory.Delete(tempProjectPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetFixes_RulesetSuppressesRule_ReturnsNoFixes()
    {
        // FixAllRulesetProject ships a custom.ruleset.json setting LC0020 to "None". Even though
        // PageA.al still has a redundant ApplicationArea, get_fixes must not offer a fix for it
        // (CodeFixRunner.FindDiagnosticAsync must honor ruleset suppression).
        var tempProjectPath = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllRulesetProject", "alcops-ruleset-getfixes-test");

        try
        {
            var filePath = Path.Combine(tempProjectPath, "PageA.al");

            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var codeFixRunner = new CodeFixRunner();
            var (analyzerResolver, loader) = CreateAnalyzerResolver();

            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);

            // Control: with the same analyzers but no ruleset applied, the location must resolve to a
            // real fixable diagnostic — proving the location itself is correct and that the ruleset
            // (not a location mismatch) is what suppresses the result below.
            const int line = 11, column = 17;
            var withoutRuleset = TestAnalyzers.LoadAnalyzersWithoutRuleset(loader, tempProjectPath);
            var controlFixes = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, withoutRuleset);
            Assert.True(controlFixes.Count > 0,
                "Expected a fixable LC0020 at line 11, column 17 with no ruleset applied — fixture or location may have changed.");

            // With the resolved AnalyzerSet (loads FixAllRulesetProject's custom.ruleset.json,
            // which sets LC0020 to "None"), the same location must yield no fixes.
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);
            var fixes = await codeFixRunner.GetFixesAsync(
                session, filePath, "LC0020", line, column, analyzerSet);

            Assert.Empty(fixes);
        }
        finally
        {
            if (Directory.Exists(tempProjectPath))
                Directory.Delete(tempProjectPath, recursive: true);
        }
    }
}
