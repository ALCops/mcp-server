using System.Text.Json;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ALCops.Mcp.Tests;

/// <summary>
/// End-to-end: apply a native code fix, then ask almcp to recompile and verify the fixed
/// diagnostic is gone. Regression guard for the "stale diagnostics after apply_fix" report
/// (PR #20 known issue). <c>al_getdiagnostics</c> was observed to return zero diagnostics on
/// this fixture because it reads the existing compilation without draining almcp's file watcher.
/// Therefore <c>al_compile</c> with <c>onlyErrors: false</c> is the only verification path
/// this suite covers.
/// </summary>
[Collection(ApplyFixAlMcpFixture.CollectionName)]
public sealed class ApplyFixThenCompileTests(ApplyFixAlMcpFixture fixture, ITestOutputHelper output) : IDisposable
{
    private CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(90));

    public void Dispose() => Cts.Dispose();

    private static IDictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static string ConcatTextContent(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [AlMcpFact]
    public async Task ApplyFix_ThenAlCompile_NoLongerReportsFixedDiagnostic()
    {
        var proxy = fixture.Proxy;
        var projectDir = fixture.ProjectDir;
        var filePath = Path.Combine(projectDir, "MyPage.al");

        // 1. Compile — LC0020 must be present before the fix
        var before = await proxy.ForwardAsync("al_compile", Args(new { onlyErrors = false }), Cts.Token);
        var beforeText = ConcatTextContent(before);
        output.WriteLine("=== al_compile BEFORE fix ===");
        output.WriteLine(beforeText);
        foreach (var logLine in fixture.Logger.Lines)
            output.WriteLine($"  [log] {logLine}");

        Assert.True(beforeText.Contains("LC0020"),
            $"Expected LC0020 in al_compile output before applying the fix.\n{beforeText}");

        // 2. Apply the fix (same path as ApplyFixToolTests.ApplyFix_WritesModifiedContentToDisk)
        using var sessionManager = new ProjectSessionManager(new ProjectLoader());
        var codeFixRunner = new CodeFixRunner();
        var loader = new ExternalAnalyzerLoader(TestAnalyzers.ToolsLocator);
        var analyzerResolver = new ProjectAnalyzerResolver(loader, new RulesetLoader());

        var session = await sessionManager.GetOrLoadProjectAsync(projectDir, Cts.Token);
        var analyzerSet = await analyzerResolver.ResolveAsync(projectDir, null, Cts.Token);

        const int line = 11, column = 17;
        var fixes = await codeFixRunner.GetFixesAsync(session, filePath, "LC0020", line, column, analyzerSet, Cts.Token);
        Assert.True(fixes.Count > 0, "Expected a fixable LC0020 at line 11, column 17.");

        var applyResult = await ApplyFixTool.ApplyFix(
            sessionManager, codeFixRunner, analyzerResolver,
            projectDir, filePath, "LC0020", line, column,
            fixes[0].EquivalenceKey, analyzers: null, Cts.Token);

        Assert.Contains("\"applied\":true", applyResult);

        // 3. Compile again — LC0020 must be gone
        fixture.Logger.Lines.Clear();
        var after = await proxy.ForwardAsync("al_compile", Args(new { onlyErrors = false }), Cts.Token);
        var afterText = ConcatTextContent(after);
        output.WriteLine("=== al_compile AFTER fix ===");
        output.WriteLine(afterText);
        foreach (var logLine2 in fixture.Logger.Lines)
            output.WriteLine($"  [log] {logLine2}");

        Assert.False(afterText.Contains("LC0020"),
            $"LC0020 still reported after applying the fix — stale diagnostics.\n{afterText}");
    }
}

/// <summary>
/// Dedicated fixture for apply-fix-then-compile tests: uses the ApplyFixProject fixture
/// (which has LC0020 at MyPage.al line 11 col 17) and its own almcp child.
/// </summary>
public sealed class ApplyFixAlMcpFixture : IAsyncLifetime
{
    public const string CollectionName = "almcp-applyfix";

    public AlMcpProxy Proxy { get; private set; } = null!;
    public CapturingLogger Logger { get; } = new();
    public string ProjectDir { get; private set; } = string.Empty;

    public static bool IsAvailable => AlMcpFixture.IsAvailable;

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
            return;

        Proxy = AlMcpFixture.CreateProxy(Logger, out var projectDir, "ApplyFixProject");
        ProjectDir = projectDir;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Proxy.StartAsync(cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (Proxy is not null)
            await Proxy.DisposeAsync();

        if (Directory.Exists(ProjectDir))
        {
            try { Directory.Delete(ProjectDir, recursive: true); }
            catch (IOException) { }
        }
    }
}

[CollectionDefinition(ApplyFixAlMcpFixture.CollectionName)]
public sealed class ApplyFixAlMcpCollection : ICollectionFixture<ApplyFixAlMcpFixture>;
