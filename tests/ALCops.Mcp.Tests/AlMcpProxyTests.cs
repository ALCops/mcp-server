using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ALCops.Mcp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// Drives <see cref="AlMcpProxy"/> against the <em>real</em> almcp from the restored DevTools
/// package. A fake MCP server would prove nothing here: what is under test is that one session
/// survives many calls and that Microsoft's own HTTP transport hands us a recoverable failure when
/// that session goes away.
/// </summary>
[Collection(AlMcpFixture.CollectionName)]
public sealed class AlMcpProxyTests(AlMcpFixture fixture) : IDisposable
{
    private const string DiagnosticsTool = "al_getdiagnostics";

    // The first al_getdiagnostics on a project compiles it, which is not instant. A ceiling rather
    // than an expectation — it only exists so a hung child fails the test instead of the run.
    private CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(60));

    public void Dispose() => Cts.Dispose();

    private static IDictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [AlMcpFact]
    public void StartAsync_DiscoversTools()
    {
        var tools = fixture.Proxy.GetCachedTools();

        Assert.NotEmpty(tools);
        Assert.Contains(tools, t => t.Name == DiagnosticsTool);
    }

    [AlMcpFact]
    public async Task ForwardAsync_RepeatedCalls_ReuseOneSession()
    {
        var sessionId = fixture.Proxy.CurrentSessionId;
        Assert.NotNull(sessionId);

        for (var i = 0; i < 3; i++)
        {
            var result = await fixture.Proxy.ForwardAsync(
                DiagnosticsTool,
                Args(new { projectPath = fixture.ProjectDir }),
                Cts.Token);

            Assert.NotEqual(true, result.IsError);
        }

        // The direct assertion that no handshake happened in between: a new session would mean a
        // new Mcp-Session-Id.
        Assert.Equal(sessionId, fixture.Proxy.CurrentSessionId);
    }

    [AlMcpFact]
    public async Task ForwardAsync_ToolError_KeepsSession()
    {
        var sessionId = fixture.Proxy.CurrentSessionId;

        // A tool-level failure (al_addproject reports Succeeded=false for a missing path) is a normal
        // MCP response, not a transport fault, so the cached client must survive it untouched.
        var failed = await fixture.Proxy.ForwardAsync(
            "al_addproject",
            Args(new { projectPath = Path.Combine(Path.GetTempPath(), $"alcops-missing-{Guid.NewGuid():N}") }),
            Cts.Token);

        Assert.NotEqual(true, failed.IsError);
        Assert.Equal(sessionId, fixture.Proxy.CurrentSessionId);

        var good = await fixture.Proxy.ForwardAsync(
            DiagnosticsTool,
            Args(new { projectPath = fixture.ProjectDir }),
            Cts.Token);

        Assert.NotEqual(true, good.IsError);
        Assert.Equal(sessionId, fixture.Proxy.CurrentSessionId);
    }

    [AlMcpFact]
    public async Task ForwardAsync_SessionEvicted_ReconnectsAndRetries()
    {
        var sessionId = fixture.Proxy.CurrentSessionId;
        Assert.NotNull(sessionId);

        fixture.Logger.Lines.Clear();

        // Deleting the session out of band is exactly what idle eviction looks like from our side:
        // the next POST with this Mcp-Session-Id gets a 404.
        using (var http = new HttpClient())
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"http://localhost:{fixture.Proxy.Port}/");
            request.Headers.Add("Mcp-Session-Id", sessionId);
            var response = await http.SendAsync(request, Cts.Token);
            Assert.True(response.IsSuccessStatusCode, $"DELETE returned {(int)response.StatusCode}");
        }

        var result = await fixture.Proxy.ForwardAsync(
            DiagnosticsTool,
            Args(new { projectPath = fixture.ProjectDir }),
            Cts.Token);

        Assert.NotEqual(true, result.IsError);
        Assert.NotEqual(sessionId, fixture.Proxy.CurrentSessionId);
        Assert.NotNull(fixture.Proxy.CurrentSessionId);
        Assert.Single(fixture.Logger.Lines, l => l.Contains("session expired; reconnecting"));
    }
}

/// <summary>
/// Skips when almcp cannot be run here — a 16.2-and-earlier toolchain, or no DevTools at all.
/// Set <c>ALCOPS_TESTS_REQUIRE_ALMCP=1</c> to turn skips into hard failures so CI never silently
/// drops these tests when the tools are supposed to be present.
/// </summary>
internal sealed class AlMcpFactAttribute : FactAttribute
{
    public AlMcpFactAttribute()
    {
        if (!AlMcpFixture.IsAvailable)
            Skip = "almcp is not runnable here (16.2 toolchain, or BC DevTools not found).";
    }
}

/// <summary>
/// Spawns one almcp child for the whole class: loading an AL project takes seconds, and the point
/// of the tests is that a single child serves many calls anyway.
/// </summary>
public sealed class AlMcpFixture : IAsyncLifetime
{
    public const string CollectionName = "almcp";

    /// <summary>
    /// The restored DevTools folder. Unlike <see cref="TestAnalyzers.ToolsLocator"/> (which points
    /// at the test output, holding only the three Private=true DLLs) this is where almcp itself is.
    /// The baked path wins when present (local dev); on CI, <c>BCDEVELOPMENTTOOLSPATH</c> is set by
    /// the workflow and <see cref="BcToolsLocator.ResolveToolsDirectory"/> picks it up.
    /// </summary>
    private static readonly string? BcToolsPath = ResolveBcToolsPath();

    private static readonly BcToolsLocator? Locator =
        string.IsNullOrEmpty(BcToolsPath) ? null : new BcToolsLocator(BcToolsPath);

    public static bool IsAvailable { get; } = Locator?.HasAlMcp == true;

    private static readonly bool RequireAlMcp =
        Environment.GetEnvironmentVariable("ALCOPS_TESTS_REQUIRE_ALMCP") is "1" or "true";

    public AlMcpProxy Proxy { get; private set; } = null!;
    public CapturingLogger Logger { get; } = new();
    public string ProjectDir { get; private set; } = string.Empty;

    private static string? ResolveBcToolsPath()
    {
        var baked = typeof(AlMcpFixture).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BcToolsPath")?.Value;
        if (!string.IsNullOrEmpty(baked) && Directory.Exists(baked))
            return baked;

        try
        {
            return BcToolsLocator.ResolveToolsDirectory();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A proxy over a fresh copy of the MinimalProject fixture, not yet started. The lifecycle tests
    /// drive start/stop themselves, so they cannot share this fixture's already-running child.
    /// </summary>
    public static AlMcpProxy CreateProxy(CapturingLogger logger, out string projectDir)
    {
        projectDir = TestAnalyzers.CopyFixtureWithAnalyzers("MinimalProject", "alcops-proxy-test");

        var loader = new ExternalAnalyzerLoader(Locator!);
        var resolver = new WorkspaceStartupResolver(
            new ProjectAnalyzerResolver(loader, new RulesetLoader()),
            loader,
            NullLogger<WorkspaceStartupResolver>.Instance,
            [projectDir]);

        return new AlMcpProxy(Locator!, resolver, logger);
    }

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            if (RequireAlMcp)
                throw new InvalidOperationException(
                    "ALCOPS_TESTS_REQUIRE_ALMCP is set but almcp is not available. " +
                    "Ensure BC DevTools are extracted and BCDEVELOPMENTTOOLSPATH is set.");
            return;
        }

        Proxy = CreateProxy(Logger, out var projectDir);
        ProjectDir = projectDir;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await Proxy.StartAsync(cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (Proxy is not null)
            await Proxy.DisposeAsync();

        if (Directory.Exists(ProjectDir))
        {
            try { Directory.Delete(ProjectDir, recursive: true); }
            catch (IOException) { /* the child may still hold a handle; the temp dir is disposable */ }
        }
    }
}

[CollectionDefinition(AlMcpFixture.CollectionName)]
public sealed class AlMcpCollection : ICollectionFixture<AlMcpFixture>;

/// <summary>
/// Records formatted log lines so a test can assert the reconnect path was taken. Ten lines beats
/// taking a dependency on Microsoft.Extensions.Logging.Testing for one assertion.
/// </summary>
public sealed class CapturingLogger : ILogger<AlMcpProxy>
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => Lines.Enqueue(formatter(state, exception));
}
