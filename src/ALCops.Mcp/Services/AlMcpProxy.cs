using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ALCops.Mcp.Services;

public sealed class AlMcpProxy : IAsyncDisposable
{
    private readonly string _almcpPath;
    private readonly WorkspaceStartupResolver _workspaceResolver;
    private readonly ILogger<AlMcpProxy> _logger;
    private readonly string[] _passthroughArgs;

    private Process? _childProcess;
    private int _port;
    private IList<McpClientTool>? _cachedTools;

    public bool IsAvailable { get; }
    public bool IsStarted => _childProcess is not null && !_childProcess.HasExited;

    public AlMcpProxy(
        BcToolsLocator toolsLocator,
        WorkspaceStartupResolver workspaceResolver,
        ILogger<AlMcpProxy> logger,
        string[]? passthroughArgs = null)
    {
        _almcpPath = toolsLocator.AlMcpPath;
        _workspaceResolver = workspaceResolver;
        _logger = logger;
        _passthroughArgs = passthroughArgs ?? [];

        // almcp ships alongside the DevTools DLLs from 17.0 onward; 16.2-and-earlier toolchains
        // resolve fine but have no almcp, in which case only our native tools are served.
        IsAvailable = toolsLocator.HasAlMcp;
        if (IsAvailable)
            _logger.LogInformation("Found almcp at: {Path}", _almcpPath);
        else
            _logger.LogWarning("almcp not found at {Path}. MS AL MCP tools will be unavailable.", _almcpPath);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return;

        _port = FindFreePort();

        var args = BuildChildArgs();
        _logger.LogInformation("Starting almcp on port {Port}: {Path} {Args}", _port, _almcpPath, string.Join(' ', args));

        var psi = new ProcessStartInfo
        {
            FileName = _almcpPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _childProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start almcp process.");

        _childProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogDebug("[almcp] {Line}", e.Data);
        };
        _childProcess.BeginErrorReadLine();

        await WaitForServerReady(cancellationToken);

        _cachedTools = await DiscoverToolsAsync(cancellationToken);
        _logger.LogInformation("Discovered {Count} tools from almcp", _cachedTools.Count);
    }

    public IList<McpClientTool> GetCachedTools() => _cachedTools ?? [];

    public async Task<CallToolResult> ForwardAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (!IsStarted)
            return ErrorResult("MS AL MCP Server is not running.");

        // Convert IDictionary<string, JsonElement> to IReadOnlyDictionary<string, object?> for CallToolAsync
        IReadOnlyDictionary<string, object?>? args = arguments?
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        McpClient? client = null;
        try
        {
            client = await CreateHttpClient(cancellationToken);
            return await client.CallToolAsync(toolName, args, null, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error forwarding tool call '{Tool}' to almcp", toolName);
            return ErrorResult($"Error calling MS AL MCP tool '{toolName}': {ex.Message}");
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
        }
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    private string[] BuildChildArgs()
    {
        // The workspace resolver supplies --projects/--codeanalyzers/--rulesetpath from the project's
        // own config; anything the user passed through on our CLI overrides it.
        return ["--port", _port.ToString(), .. _workspaceResolver.BuildAlMcpArgs(_passthroughArgs)];
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task WaitForServerReady(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var timeout = TimeSpan.FromSeconds(30);
        var start = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(start) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_childProcess is null || _childProcess.HasExited)
                throw new InvalidOperationException($"almcp process exited unexpectedly with code {_childProcess?.ExitCode}.");

            try
            {
                // A bare POST to the MCP endpoint is rejected (400/406) but proves it is mapped.
                // A 404 means Kestrel is up but we are asking for the wrong path, which must not
                // count as ready — otherwise the failure only surfaces later, as a confusing
                // handshake error.
                var response = await http.PostAsync(McpEndpoint, null, cancellationToken);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    _logger.LogInformation("almcp server ready on port {Port}", _port);
                    return;
                }

                throw new InvalidOperationException(
                    $"almcp is listening on port {_port} but serves no MCP endpoint at {McpEndpoint}.");
            }
            catch (HttpRequestException)
            {
                // Not listening yet
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"almcp did not become ready within {timeout.TotalSeconds}s on port {_port}.");
    }

    private async Task<IList<McpClientTool>> DiscoverToolsAsync(CancellationToken cancellationToken)
    {
        await using var client = await CreateHttpClient(cancellationToken);
        return await client.ListToolsAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// almcp calls <c>MapMcp()</c> with no pattern, so the streamable-HTTP endpoint is the server
    /// root — not <c>/mcp/</c>, which 404s.
    /// </summary>
    private Uri McpEndpoint => new($"http://localhost:{_port}/");

    private async Task<McpClient> CreateHttpClient(CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = McpEndpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "almcp-proxy",
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_childProcess is not null && !_childProcess.HasExited)
        {
            _logger.LogInformation("Stopping almcp process (PID {Pid})", _childProcess.Id);
            try
            {
                _childProcess.Kill(entireProcessTree: true);
                await _childProcess.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping almcp process");
            }
        }

        _childProcess?.Dispose();
        _childProcess = null;
    }
}
