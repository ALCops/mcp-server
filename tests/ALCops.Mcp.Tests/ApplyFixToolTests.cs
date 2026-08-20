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
    private static string GetFixturePath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Test fixture '{name}' not found at {path}. Ensure fixtures are copied to output.");
        return path;
    }

    private static ProjectAnalyzerResolver CreateAnalyzerResolver(AnalyzerRegistry registry)
    {
        var devToolsLocator = new DevToolsLocator();
        var alExtensionLocator = new AlExtensionLocator();
        var nugetDownloader = new NuGetDevToolsDownloader();
        var externalLoader = new ExternalAnalyzerLoader(alExtensionLocator, nugetDownloader, devToolsLocator);
        var rulesetLoader = new RulesetLoader();
        return new ProjectAnalyzerResolver(registry, externalLoader, rulesetLoader);
    }

    [Fact]
    public async Task ApplyFix_WritesModifiedContentToDisk()
    {
        // Copy the fixture to a temp directory so this test can safely mutate the file
        // without affecting the checked-in fixture used by other tests/runs.
        var fixtureSource = GetFixturePath("ApplyFixProject");
        var tempProjectPath = Path.Combine(Path.GetTempPath(), $"alcops-applyfix-test-{Guid.NewGuid():N}");
        CopyDirectory(fixtureSource, tempProjectPath);

        try
        {
            var filePath = Path.Combine(tempProjectPath, "MyPage.al");
            var originalContent = await File.ReadAllTextAsync(filePath);

            var sessionManager = new ProjectSessionManager(new ProjectLoader(new DevToolsLocator()));
            var registry = new AnalyzerRegistry();
            var codeFixRunner = new CodeFixRunner(registry);
            var analyzerResolver = CreateAnalyzerResolver(registry);

            var diagnosticsRunner = new DiagnosticsRunner(registry);
            var session = await sessionManager.GetOrLoadProjectAsync(tempProjectPath);
            var analyzerSet = await analyzerResolver.ResolveAsync(tempProjectPath, null);
            var diagnostics = await diagnosticsRunner.RunAsync(session, filePath: filePath, analyzerProvider: analyzerSet);

            var target = diagnostics.FirstOrDefault(d => d.Id == "LC0020");
            Assert.True(target is not null,
                $"Expected fixture to produce an LC0020 (ApplicationAreaRedundancy) diagnostic — got: " +
                $"{string.Join(", ", diagnostics.Select(d => d.Id))}. Built-in analyzers may not be loaded in this environment.");

            var fixes = await codeFixRunner.GetFixesAsync(
                session, filePath, target!.Id, target.StartLine, target.StartColumn,
                analyzerProvider: analyzerSet);
            Assert.True(fixes.Count > 0, "Expected at least one available code fix for LC0020.");

            var result = await ApplyFixTool.ApplyFix(
                sessionManager, codeFixRunner, analyzerResolver,
                tempProjectPath, filePath, target.Id, target.StartLine, target.StartColumn,
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

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
    }
}
