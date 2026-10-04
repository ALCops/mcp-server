using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Dynamics.Nav.CodeAnalysis.CodeFixes;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Every equivalence key the tools advertise (Ambiguous candidates, get_fixes fixes) must be accepted
/// back verbatim by apply_fix and apply_fix_all. AC0012 in FixAllProject has two fix providers.
/// </summary>
public sealed class FixRoundTripTests
{
    private const string Rule = "AC0012";

    [Fact]
    public async Task AdvertisedKeys_RoundTrip_ThroughApplyFixAllGetFixesAndApplyFix()
    {
        var projectPath = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllProject", "alcops-roundtrip-test");
        try
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var runner = new CodeFixRunner();
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();
            var writer = new GuardedFileWriter();

            // 1. No key: Ambiguous, with the candidates.
            var ambiguous = await ApplyFixAllTool.ApplyFixAll(
                sessionManager, runner, analyzerResolver, writer, projectPath, Rule);
            var candidateKeys = ToolResultAssert.Error(ambiguous, "Ambiguous")
                .GetProperty("candidates").EnumerateArray()
                .Select(c => c.GetProperty("equivalenceKey").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(candidateKeys.Count > 1);

            // 2. Every candidate key is accepted by apply_fix_all.
            foreach (var key in candidateKeys)
            {
                var dryRun = await ApplyFixAllTool.ApplyFixAll(
                    sessionManager, runner, analyzerResolver, writer, projectPath, Rule,
                    equivalenceKey: key, dryRun: true);
                var root = ToolResultAssert.Ok(dryRun);
                Assert.Equal(key, root.GetProperty("equivalenceKey").GetString());
            }

            // 3. Locate one occurrence, then get_fixes there advertises the same key set.
            var session = await sessionManager.GetOrLoadProjectAsync(projectPath);
            var analyzerSet = await analyzerResolver.ResolveAsync(projectPath, null);
            var probe = await runner.ApplyFixAllAsync(
                session, Rule, FixAllScope.Project, null, candidateKeys.First(), analyzerSet);
            Assert.Equal(FixAllStatus.Completed, probe.Status);
            var occurrence = probe.Changes[0].Diagnostics[0];

            var getFixes = await GetFixesTool.GetFixes(
                sessionManager, runner, analyzerResolver,
                projectPath, occurrence.FilePath, Rule, occurrence.Line, occurrence.Column);
            var fixKeys = ToolResultAssert.OkAs<GetFixesResult>(getFixes).Fixes
                .Select(f => f.EquivalenceKey)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(candidateKeys.Order(StringComparer.Ordinal), fixKeys.Order(StringComparer.Ordinal));

            // 4. Every key is accepted by apply_fix, each on a fresh copy.
            var relativeFile = Path.GetRelativePath(projectPath, occurrence.FilePath);
            foreach (var key in fixKeys)
                await ApplyFixOnFreshCopyAsync(relativeFile, occurrence.Line, occurrence.Column, key);
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(projectPath);
        }
    }

    private static async Task ApplyFixOnFreshCopyAsync(string relativeFile, int line, int column, string key)
    {
        var projectPath = TestAnalyzers.CopyFixtureWithAnalyzers("FixAllProject", "alcops-roundtrip-apply-test");
        try
        {
            using var sessionManager = new ProjectSessionManager(new ProjectLoader());
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, new CodeFixRunner(), analyzerResolver, new GuardedFileWriter(),
                projectPath, Path.Combine(projectPath, relativeFile), Rule, line, column, key);

            Assert.True(ToolResultAssert.Ok(result).GetProperty("applied").GetBoolean(),
                $"apply_fix rejected advertised key '{key}': {ToolResultAssert.Text(result)}");
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(projectPath);
        }
    }
}
