using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class DiagnosticsRunnerTests
{
    private static string GetFixturePath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Test fixture '{name}' not found at {path}. Ensure fixtures are copied to output.");
        return path;
    }

    private static ProjectLoader CreateLoader()
    {
        var locator = new DevToolsLocator();
        return new ProjectLoader(locator);
    }

    [Fact]
    public async Task RunAsync_PragmaSuppressedDiagnostics_AreNotReported()
    {
        var loader = CreateLoader();
        var projectPath = GetFixturePath("PragmaProject");
        var session = await loader.LoadProjectAsync(projectPath);
        var registry = new AnalyzerRegistry();
        var runner = new DiagnosticsRunner(registry);

        var withoutPragmaPath = Path.Combine(projectPath, "WithoutPragma.al");
        var withPragmaPath = Path.Combine(projectPath, "WithPragma.al");

        var withoutPragmaResults = await runner.RunAsync(session, filePath: withoutPragmaPath);
        var withPragmaResults = await runner.RunAsync(session, filePath: withPragmaPath);

        // The unsuppressed file must produce diagnostics for the test to be meaningful.
        Assert.True(withoutPragmaResults.Count > 0,
            "No diagnostics produced for the unsuppressed file — " +
            "built-in analyzers may not be loaded in this environment.");

        Assert.True(
            withPragmaResults.Count < withoutPragmaResults.Count,
            $"Expected pragma-suppressed file to produce fewer diagnostics " +
            $"({withPragmaResults.Count}) than unsuppressed file ({withoutPragmaResults.Count}). " +
            $"Unsuppressed diagnostics: {string.Join(", ", withoutPragmaResults.Select(d => d.Id))}. " +
            $"Pragma-suppressed diagnostics: {string.Join(", ", withPragmaResults.Select(d => d.Id))}.");
    }
}
