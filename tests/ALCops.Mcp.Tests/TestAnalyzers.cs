using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Shared fixture plumbing for the code-fix tests.
///
/// <para>The server no longer bundles analyzers — it resolves them from the project's own
/// <c>al.codeAnalyzers</c> setting. So each fixture has to declare its analyzers like a real AL
/// project would, which means these tests now exercise exactly the path production uses:
/// spec → <see cref="ExternalAnalyzerLoader"/> → <see cref="AnalyzerAssemblyLoadContext"/>.</para>
/// </summary>
internal static class TestAnalyzers
{
    /// <summary>
    /// The ALCops cop DLLs, referenced by the test project only (never shipped) and therefore
    /// sitting next to the test binary. The fixtures' rules (LC0020, AC0012) come from these.
    /// </summary>
    private static readonly string[] CopDllNames =
    [
        "ALCops.ApplicationCop.dll",
        "ALCops.DocumentationCop.dll",
        "ALCops.FormattingCop.dll",
        "ALCops.LinterCop.dll",
        "ALCops.PlatformCop.dll",
        "ALCops.TestAutomationCop.dll",
    ];

    /// <summary>
    /// The tools directory for the test process: the test output folder, which holds the BC DevTools
    /// DLLs (Private=true, and hot-swapped per SDK version by the CI matrix) alongside the cops.
    /// </summary>
    public static BcToolsLocator ToolsLocator { get; } = new(AppContext.BaseDirectory);

    public static IReadOnlyList<string> CopDllPaths { get; } =
        [.. CopDllNames.Select(n => Path.Combine(AppContext.BaseDirectory, n)).Where(File.Exists)];

    public static string GetFixturePath(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Test fixture '{name}' not found at {path}. Ensure fixtures are copied to output.");
        return path;
    }

    /// <summary>
    /// Copies a fixture to a temp directory and gives it an <c>al.codeAnalyzers</c> setting pointing
    /// at the cop DLLs in the test output. Written here rather than checked in because the paths are
    /// absolute and only known at run time.
    /// </summary>
    public static string CopyFixtureWithAnalyzers(string fixtureName, string tempPrefix)
    {
        var destination = Path.Combine(Path.GetTempPath(), $"{tempPrefix}-{Guid.NewGuid():N}");
        CopyDirectory(GetFixturePath(fixtureName), destination);
        WriteAnalyzerSettings(destination);
        return destination;
    }

    public static void WriteAnalyzerSettings(string projectPath)
    {
        Assert.NotEmpty(CopDllPaths);

        var vscodeDir = Path.Combine(projectPath, ".vscode");
        Directory.CreateDirectory(vscodeDir);

        var entries = string.Join(",\n", CopDllPaths.Select(p => $"    {JsonEscape(p)}"));
        File.WriteAllText(
            Path.Combine(vscodeDir, "settings.json"),
            $"{{\n  \"al.codeAnalyzers\": [\n{entries}\n  ]\n}}\n");
    }

    /// <summary>
    /// An <see cref="AnalyzerSet"/> with the fixture's analyzers loaded but no ruleset applied —
    /// the control case for tests that assert a ruleset is what suppresses a diagnostic.
    /// </summary>
    public static AnalyzerSet LoadAnalyzersWithoutRuleset(ExternalAnalyzerLoader loader, string projectPath)
    {
        var assemblies = CopDllPaths
            .Select(p => loader.ResolveAndLoad(AnalyzerSpec.Parse(p), projectPath))
            .OfType<LoadedAnalyzerAssembly>()
            .ToList();

        return new AnalyzerSet(assemblies);
    }

    public static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destDir, Path.GetFileName(directory)));
    }

    private static string JsonEscape(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
