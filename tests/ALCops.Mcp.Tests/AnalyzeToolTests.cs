using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace ALCops.Mcp.Tests;

public sealed class AnalyzeToolTests
{
    [Fact]
    public async Task ProxyUnavailable_WhenNoProxy()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();
        var resolver = new WorkspaceStartupResolver(
            analyzerResolver,
            new ExternalAnalyzerLoader(TestAnalyzers.ToolsLocator),
            NullLogger<WorkspaceStartupResolver>.Instance,
            ["C:\\dummy"]);

        var json = await AnalyzeTool.Analyze(services, analyzerResolver, resolver);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("ProxyUnavailable", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("--no-proxy", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProxyUnavailable_WhenAlmcpNotFound()
    {
        var toolsDir = Path.Combine(Path.GetTempPath(), $"alcops-noalmcp-analyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "Microsoft.Dynamics.Nav.CodeAnalysis.dll"), "stub");

        try
        {
            var locator = new BcToolsLocator(toolsDir);
            Assert.False(locator.HasAlMcp);

            var (analyzerResolver, loader) = TestAnalyzers.CreateAnalyzerResolver(locator);
            var resolver = new WorkspaceStartupResolver(
                analyzerResolver,
                loader,
                NullLogger<WorkspaceStartupResolver>.Instance,
                []);

            var proxy = new AlMcpProxy(locator, resolver, new CapturingLogger());

            var sc = new ServiceCollection();
            sc.AddSingleton(proxy);
            var sp = sc.BuildServiceProvider();

            var json = await AnalyzeTool.Analyze(sp, analyzerResolver, resolver);
            var doc = JsonDocument.Parse(json);

            Assert.Equal("ProxyUnavailable", doc.RootElement.GetProperty("error").GetString());
            Assert.Contains("not available", doc.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(toolsDir);
        }
    }

    [Fact]
    public void Schema_ExposesUserParameters_HidesServicesAndCancellationToken()
    {
        var method = typeof(AnalyzeTool).GetMethod(nameof(AnalyzeTool.Analyze))!;
        var tool = McpServerTool.Create(method);

        var schema = tool.ProtocolTool.InputSchema;
        var schemaJson = JsonSerializer.Serialize(schema);
        using var doc = JsonDocument.Parse(schemaJson);
        var properties = doc.RootElement.GetProperty("properties");
        var paramNames = properties.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.DoesNotContain("services", paramNames);
        Assert.DoesNotContain("cancellationToken", paramNames);

        string[] expected = ["filePath", "folderPath", "projectPath", "severities", "analyzers", "ruleIds", "limit"];
        foreach (var name in expected)
            Assert.Contains(name, paramNames);
    }
}

[Collection(AnalyzeAlMcpFixture.CollectionName)]
public sealed class AnalyzeToolIntegrationTests(AnalyzeAlMcpFixture fixture, ITestOutputHelper output) : IDisposable
{
    private CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(90));

    public void Dispose() => Cts.Dispose();

    private async Task<AnalyzeResult> RunAnalyze(
        string? projectPath = null,
        string? filePath = null,
        string? folderPath = null,
        string[]? severities = null,
        string[]? analyzers = null,
        string[]? ruleIds = null,
        int limit = AnalyzeTool.DefaultLimit)
    {
        var json = await AnalyzeTool.Analyze(
            fixture.ServiceProvider,
            fixture.AnalyzerResolver,
            fixture.WorkspaceResolver,
            filePath, folderPath, projectPath,
            severities, analyzers, ruleIds, limit, Cts.Token);

        output.WriteLine(json);

        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("error", out var err),
            $"Unexpected error: {err}");

        return JsonSerializer.Deserialize<AnalyzeResult>(json, JsonDefaults.Options)!;
    }

    [AlMcpFact]
    public async Task ProjectScoped_ReturnsOnlyApplyFixProjectDiagnostics()
    {
        var result = await RunAnalyze(projectPath: fixture.ProjA);

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.True(
                d.FilePath.StartsWith(fixture.ProjA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Expected path under {fixture.ProjA}, got {d.FilePath}");
        });

        Assert.True(result.Summary.ByAnalyzer.Count >= 1);
        Assert.Equal(result.Count, result.TotalCount);
        Assert.False(result.Truncated);

        Assert.DoesNotContain(result.Diagnostics, d =>
            d.FilePath is not null && d.FilePath.StartsWith(fixture.ProjB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [AlMcpFact]
    public async Task DefaultScope_ReturnsDiagnosticsFromPrimaryProject()
    {
        var result = await RunAnalyze();

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.True(
                d.FilePath.StartsWith(fixture.ProjA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Expected path under primary project {fixture.ProjA}, got {d.FilePath}");
        });
    }

    [AlMcpFact]
    public async Task ExplicitProjB_ReturnsDiagnosticsFromProjBOnly()
    {
        var result = await RunAnalyze(projectPath: fixture.ProjB);

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.True(
                d.FilePath.StartsWith(fixture.ProjB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Expected path under {fixture.ProjB}, got {d.FilePath}");
        });
    }

    [AlMcpFact]
    public async Task FilterByRuleIdAndSeverityAndProject_ReturnsLC0020Hits()
    {
        var result = await RunAnalyze(
            projectPath: fixture.ProjB,
            severities: ["warning"],
            ruleIds: ["LC0020"]);

        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal("LC0020", d.Id);
            Assert.Equal("Warning", d.Severity);
            Assert.True(
                d.FilePath!.StartsWith(fixture.ProjB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Expected path under {fixture.ProjB}, got {d.FilePath}");
        });

        var fileNames = result.Diagnostics.Select(d => Path.GetFileName(d.FilePath!)).ToList();
        Assert.Contains("PageA.al", fileNames);
        Assert.Contains("PageB.al", fileNames);

        for (var i = 1; i < result.Diagnostics.Count; i++)
        {
            var prev = result.Diagnostics[i - 1];
            var curr = result.Diagnostics[i];
            Assert.True(
                string.Compare(prev.FilePath, curr.FilePath, StringComparison.OrdinalIgnoreCase) <= 0,
                $"Not sorted: {prev.FilePath} > {curr.FilePath}");
        }
    }

    [AlMcpFact]
    public async Task Limit_Truncates()
    {
        var result = await RunAnalyze(limit: 1);

        Assert.Equal(1, result.Count);
        Assert.True(result.TotalCount > 1);
        Assert.True(result.Truncated);
    }

    [AlMcpFact]
    public async Task AnalyzersFilter_Compiler()
    {
        var result = await RunAnalyze(analyzers: ["Compiler"]);

        Assert.All(result.Diagnostics, d =>
            Assert.Matches("^AL\\d{4}$", d.Id));
    }

    [AlMcpFact]
    public async Task InvalidLimit_ReturnsError()
    {
        var json = await AnalyzeTool.Analyze(
            fixture.ServiceProvider,
            fixture.AnalyzerResolver,
            fixture.WorkspaceResolver,
            limit: 0,
            cancellationToken: Cts.Token);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("InvalidLimit", doc.RootElement.GetProperty("error").GetString());
    }

}

public sealed class AnalyzeAlMcpFixture : IAsyncLifetime
{
    public const string CollectionName = "almcp-analyze";

    public AlMcpProxy Proxy { get; private set; } = null!;
    public CapturingLogger Logger { get; } = new();
    public string ProjA { get; private set; } = string.Empty;
    public string ProjB { get; private set; } = string.Empty;
    public ProjectAnalyzerResolver AnalyzerResolver { get; private set; } = null!;
    public WorkspaceStartupResolver WorkspaceResolver { get; private set; } = null!;
    public IServiceProvider ServiceProvider { get; private set; } = null!;

    private string _tempRoot = string.Empty;

    public static bool IsAvailable => AlMcpFixture.IsAvailable;

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
            return;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"alcops-analyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        ProjA = CopyFixtureInto("ApplyFixProject", "ProjA");
        ProjB = CopyFixtureInto("FixAllProject", "ProjB");

        var (analyzerResolver, loader) = TestAnalyzers.CreateAnalyzerResolver();
        AnalyzerResolver = analyzerResolver;

        WorkspaceResolver = new WorkspaceStartupResolver(
            analyzerResolver,
            loader,
            NullLogger<WorkspaceStartupResolver>.Instance,
            [ProjA, ProjB]);

        Proxy = AlMcpFixture.CreateProxy(Logger, [ProjA, ProjB]);

        var sc = new ServiceCollection();
        sc.AddSingleton(Proxy);
        ServiceProvider = sc.BuildServiceProvider();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Proxy.StartAsync(cts.Token);
    }

    private string CopyFixtureInto(string fixtureName, string subDir)
    {
        var dest = Path.Combine(_tempRoot, subDir);
        TestAnalyzers.CopyDirectory(TestAnalyzers.GetFixturePath(fixtureName), dest);
        TestAnalyzers.WriteAnalyzerSettings(dest);
        return dest;
    }

    public async Task DisposeAsync()
    {
        if (Proxy is not null)
            await Proxy.DisposeAsync();

        TestAnalyzers.TryDeleteDirectory(_tempRoot);
    }
}

[CollectionDefinition(AnalyzeAlMcpFixture.CollectionName)]
public sealed class AnalyzeAlMcpCollection : ICollectionFixture<AnalyzeAlMcpFixture>;
