using System.Text.Json;
using ALCops.Mcp.Models;
using ALCops.Mcp.Services;
using ALCops.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace ALCops.Mcp.Tests;

public sealed class AnalyzeToolTests
{
    private static WorkspaceStartupResolver DummyResolver(ProjectAnalyzerResolver analyzerResolver) =>
        new(analyzerResolver,
            new ExternalAnalyzerLoader(TestAnalyzers.ToolsLocator),
            NullLogger<WorkspaceStartupResolver>.Instance,
            ["C:\\dummy"]);

    [Fact]
    public async Task Unavailable_NoProxy_WhenServerRunsWithNoProxy()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

        var result = await AnalyzeTool.Analyze(services, analyzerResolver, DummyResolver(analyzerResolver));

        var root = ToolResultAssert.Error(result, "Unavailable", "NoProxy");
        Assert.Contains("--no-proxy", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Unavailable_AlmcpNotFound_WhenToolsDirHasNoAlmcp()
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

            var result = await AnalyzeTool.Analyze(sp, analyzerResolver, resolver);

            var root = ToolResultAssert.Error(result, "Unavailable", "AlmcpNotFound");
            Assert.Contains("not found", root.GetProperty("message").GetString());
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(toolsDir);
        }
    }

    [Fact]
    public async Task Unavailable_AlmcpNotReady_WhenAlmcpStopped()
    {
        var toolsDir = CreateStubToolsDirWithAlmcp();

        try
        {
            var locator = new BcToolsLocator(toolsDir);
            Assert.True(locator.HasAlMcp);

            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();
            var resolver = DummyResolver(analyzerResolver);

            // Never started; disposing completes Ready with false, as a failed start does.
            var proxy = new AlMcpProxy(locator, resolver, new CapturingLogger());
            await proxy.DisposeAsync();

            var sc = new ServiceCollection();
            sc.AddSingleton(proxy);

            var result = await AnalyzeTool.Analyze(sc.BuildServiceProvider(), analyzerResolver, resolver);

            ToolResultAssert.Error(result, "Unavailable", "AlmcpNotReady");
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(toolsDir);
        }
    }

    [Fact]
    public async Task Cancellation_Propagates_InsteadOfFaulted()
    {
        var toolsDir = CreateStubToolsDirWithAlmcp();

        try
        {
            var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();
            var resolver = DummyResolver(analyzerResolver);

            // Never started and not disposed: Ready stays pending, so the cancelled wait throws.
            var proxy = new AlMcpProxy(new BcToolsLocator(toolsDir), resolver, new CapturingLogger());

            var sc = new ServiceCollection();
            sc.AddSingleton(proxy);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AnalyzeTool.Analyze(sc.BuildServiceProvider(), analyzerResolver, resolver, cancellationToken: cts.Token));

            await proxy.DisposeAsync();
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(toolsDir);
        }
    }

    [Fact]
    public void MapProxyFailure_IsUnavailableAlmcpCallFailed_WithAlmcpTextAsDetail()
    {
        var failed = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Session not found" }]
        };

        var result = AnalyzeTool.MapProxyFailure(failed);

        var root = ToolResultAssert.Error(result, "Unavailable", "AlmcpCallFailed");
        Assert.Equal("Session not found", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Invalid_WhenLimitIsNotPositive()
    {
        // The limit check runs before the proxy checks, so this needs no almcp.
        var services = new ServiceCollection().BuildServiceProvider();
        var (analyzerResolver, _) = TestAnalyzers.CreateAnalyzerResolver();

        var result = await AnalyzeTool.Analyze(services, analyzerResolver, DummyResolver(analyzerResolver), limit: 0);

        var root = ToolResultAssert.Error(result, "Invalid");
        Assert.Contains("limit", root.GetProperty("message").GetString());
    }

    /// <summary>A tools directory that BcToolsLocator accepts as having almcp; nothing in it can run.</summary>
    private static string CreateStubToolsDirWithAlmcp()
    {
        var toolsDir = Path.Combine(Path.GetTempPath(), $"alcops-stubalmcp-analyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "Microsoft.Dynamics.Nav.CodeAnalysis.dll"), "stub");
        File.WriteAllText(Path.Combine(toolsDir, "almcp.exe"), "stub");
        File.WriteAllText(Path.Combine(toolsDir, "almcp.dll"), "stub");
        return toolsDir;
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
        var result = await AnalyzeTool.Analyze(
            fixture.ServiceProvider,
            fixture.AnalyzerResolver,
            fixture.WorkspaceResolver,
            filePath, folderPath, projectPath,
            severities, analyzers, ruleIds, limit, Cts.Token);

        output.WriteLine(ToolResultAssert.Text(result));

        return ToolResultAssert.OkAs<AnalyzeResult>(result);
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
        var located = result.Diagnostics.Where(d => d.FilePath is not null).ToList();
        Assert.NotEmpty(located);
        Assert.All(located, d =>
            Assert.True(
                d.FilePath!.StartsWith(fixture.ProjA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Expected path under primary project {fixture.ProjA}, got {d.FilePath}"));
        Assert.Equal(fixture.ProjA, result.Project, ignoreCase: true);

        Assert.DoesNotContain(result.Diagnostics, d =>
            d.FilePath is not null && d.FilePath.StartsWith(fixture.ProjB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
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
    public async Task FilePathInProjB_NoProjectPath_ReturnsProjBDiagnosticsOnly()
    {
        var pageA = Path.Combine(fixture.ProjB, "PageA.al");
        var result = await RunAnalyze(filePath: pageA);

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.Equal(pageA, d.FilePath, ignoreCase: true);
        });
        Assert.Contains(result.Diagnostics, d => d.Analyzer == "ALCops.LinterCop");
        Assert.Equal(fixture.ProjB, result.Project, ignoreCase: true);
    }

    [AlMcpFact]
    public async Task FolderPathProjB_NoProjectPath_ReturnsProjBDiagnostics()
    {
        var result = await RunAnalyze(folderPath: fixture.ProjB);

        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.True(
                d.FilePath.StartsWith(fixture.ProjB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"Expected path under {fixture.ProjB}, got {d.FilePath}");
        });

        var fileNames = result.Diagnostics.Select(d => Path.GetFileName(d.FilePath!)).Distinct().ToList();
        Assert.Contains("PageA.al", fileNames);
        Assert.Contains("PageB.al", fileNames);
        Assert.Equal(fixture.ProjB, result.Project, ignoreCase: true);

        Assert.DoesNotContain(result.Diagnostics, d =>
            d.FilePath is not null && d.FilePath.StartsWith(fixture.ProjA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [AlMcpFact]
    public async Task UnknownProject_ReturnsInvalid()
    {
        var bogus = Path.Combine(Path.GetDirectoryName(fixture.ProjA)!, "NonExistent");
        var result = await AnalyzeTool.Analyze(
            fixture.ServiceProvider,
            fixture.AnalyzerResolver,
            fixture.WorkspaceResolver,
            projectPath: bogus,
            cancellationToken: Cts.Token);

        output.WriteLine(ToolResultAssert.Text(result));
        var root = ToolResultAssert.Error(result, "Invalid");

        var message = root.GetProperty("message").GetString()!;
        Assert.Contains(fixture.ProjA, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.ProjB, message, StringComparison.OrdinalIgnoreCase);
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
