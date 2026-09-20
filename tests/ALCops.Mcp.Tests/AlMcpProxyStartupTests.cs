using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using ALCops.Mcp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace ALCops.Mcp.Tests;

/// <summary>
/// The startup contract: <see cref="AlMcpProxyStartup"/> must hand control back to the host
/// immediately so the stdio MCP server can answer <c>initialize</c>, and everything that needs
/// almcp must go through <see cref="AlMcpProxy.Ready"/> instead of a blocked startup.
///
/// <para>Each test owns its own child process — the lifecycle is what is under test — but the class
/// shares the <c>almcp</c> collection so it never spawns children in parallel with
/// <see cref="AlMcpProxyTests"/>.</para>
/// </summary>
[Collection(AlMcpFixture.CollectionName)]
public sealed class AlMcpProxyStartupTests : IDisposable
{
    private const string DiagnosticsTool = "al_getdiagnostics";

    private CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(90));

    public void Dispose() => Cts.Dispose();

    private static IDictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static AlMcpProxyStartup NewStartup(AlMcpProxy proxy) =>
        new(proxy, NullLogger<AlMcpProxyStartup>.Instance);

    [AlMcpFact]
    public async Task StartAsync_ReturnsBeforeAlmcpIsReady()
    {
        var proxy = AlMcpFixture.CreateProxy(new CapturingLogger(), out var projectDir);
        var startup = NewStartup(proxy);

        try
        {
            var elapsed = Stopwatch.GetTimestamp();
            await startup.StartAsync(Cts.Token);
            var returned = Stopwatch.GetElapsedTime(elapsed);

            // No child process can have launched Kestrel and served a tool list in this window.
            Assert.True(returned < TimeSpan.FromSeconds(2), $"StartAsync took {returned}");
            Assert.False(proxy.Ready.IsCompleted);

            Assert.True(await proxy.Ready.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.True(proxy.IsReady);
            Assert.NotEmpty(proxy.GetCachedTools());
        }
        finally
        {
            await startup.StopAsync(Cts.Token);
            TestAnalyzers.TryDeleteDirectory(projectDir);
        }
    }

    [AlMcpFact]
    public async Task ForwardAsync_BeforeReady_WaitsThenForwards()
    {
        var proxy = AlMcpFixture.CreateProxy(new CapturingLogger(), out var projectDir);
        var startup = NewStartup(proxy);

        try
        {
            await startup.StartAsync(Cts.Token);

            // Issued while the child is still booting: the call must park on Ready, not fail fast.
            var result = await proxy.ForwardAsync(
                DiagnosticsTool,
                Args(new { projectPath = projectDir }),
                Cts.Token);

            Assert.NotEqual(true, result.IsError);
            Assert.True(proxy.IsReady);
        }
        finally
        {
            await startup.StopAsync(Cts.Token);
            TestAnalyzers.TryDeleteDirectory(projectDir);
        }
    }

    [AlMcpFact]
    public async Task StopAsync_DuringStartup_CancelsAndStopsChild()
    {
        var proxy = AlMcpFixture.CreateProxy(new CapturingLogger(), out var projectDir);
        var startup = NewStartup(proxy);

        try
        {
            await startup.StartAsync(Cts.Token);

            // Stop once the child exists but is still loading the project, so this really exercises
            // cancelling the readiness poll rather than a start that never began.
            while (!proxy.IsStarted && !proxy.Ready.IsCompleted)
                await Task.Delay(10, Cts.Token);

            Assert.False(proxy.Ready.IsCompleted, "almcp became ready before the test could stop it");

            var elapsed = Stopwatch.GetTimestamp();
            await startup.StopAsync(Cts.Token);
            var stopped = Stopwatch.GetElapsedTime(elapsed);

            // Shutdown must not sit out almcp's own 30s readiness budget.
            Assert.True(stopped < TimeSpan.FromSeconds(15), $"StopAsync took {stopped}");
            Assert.True(proxy.Ready.IsCompleted);
            Assert.False(await proxy.Ready);
            Assert.False(proxy.IsStarted);
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(projectDir);
        }
    }

    /// <summary>
    /// The only one of these that needs no almcp: a toolchain without it must settle
    /// <see cref="AlMcpProxy.Ready"/> at construction, or every <c>al_*</c> call would hang forever
    /// now that <c>ForwardAsync</c> waits on it.
    /// </summary>
    [Fact]
    public async Task Ready_WhenAlmcpUnavailable_IsFalseImmediately()
    {
        var toolsDir = Path.Combine(Path.GetTempPath(), $"alcops-noalmcp-{Guid.NewGuid():N}");
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
            var startup = NewStartup(proxy);

            Assert.True(proxy.Ready.IsCompleted);
            Assert.False(await proxy.Ready);
            Assert.False(proxy.IsReady);

            await startup.StartAsync(Cts.Token);

            var result = await proxy.ForwardAsync("al_compile", null, Cts.Token);

            Assert.True(result.IsError);
            Assert.Contains(
                "not available",
                string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text)));

            await startup.StopAsync(Cts.Token);
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(toolsDir);
        }
    }

    /// <summary>
    /// A host that lists tools twice while almcp boots must still be pinged once, not twice — the
    /// notification is the only thing that gets the <c>al_*</c> tools in front of it.
    /// </summary>
    [AlMcpFact]
    public async Task NotifyToolListChangedWhenReady_ArmsOnce()
    {
        var proxy = AlMcpFixture.CreateProxy(new CapturingLogger(), out var projectDir);
        var startup = NewStartup(proxy);

        // Nothing is ever written to the input pipe, so the transport's read loop parks instead of
        // hitting EOF and disconnecting; the output pipe is what we assert on.
        var input = new Pipe();
        var output = new Pipe();
        await using var transport = new StreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream());
        await using var server = McpServer.Create(transport, new McpServerOptions());

        try
        {
            proxy.NotifyToolListChangedWhenReady(server);
            proxy.NotifyToolListChangedWhenReady(server);

            await startup.StartAsync(Cts.Token);
            Assert.True(await proxy.Ready.WaitAsync(TimeSpan.FromSeconds(60)));

            var written = await DrainAsync(output.Reader, TimeSpan.FromSeconds(5));

            Assert.Equal(1, CountOccurrences(written, "notifications/tools/list_changed"));
        }
        finally
        {
            await startup.StopAsync(Cts.Token);
            TestAnalyzers.TryDeleteDirectory(projectDir);
        }
    }

    /// <summary>Collects everything written to <paramref name="reader"/> during a fixed window.</summary>
    private static async Task<string> DrainAsync(PipeReader reader, TimeSpan window)
    {
        var text = new StringBuilder();
        using var cts = new CancellationTokenSource(window);

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cts.Token);
                foreach (var segment in result.Buffer)
                    text.Append(Encoding.UTF8.GetString(segment.Span));

                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The window is the point: nothing more is expected after it.
        }

        return text.ToString();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
