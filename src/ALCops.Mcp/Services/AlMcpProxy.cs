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

    // One MCP session for the whole life of the child. A per-call client cost four round-trips
    // (initialize, initialized, tools/call, DELETE) where one suffices. The price of keeping it is
    // that we now have to survive the server evicting an idle session — see ForwardAsync.
    private McpClient? _client;
    private readonly SemaphoreSlim _clientGate = new(1, 1);

    public bool IsAvailable { get; }
    public bool IsStarted => _childProcess is not null && !_childProcess.HasExited;

    /// <summary>The current almcp session id, for tests that need to end the session out of band.</summary>
    internal string? CurrentSessionId => _client?.SessionId;

    internal int Port => _port;

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

        _client = await CreateHttpClient(cancellationToken);
        _cachedTools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
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

        McpClient client;
        try
        {
            client = await GetOrCreateClientAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Connecting failed outright — report it the same way a failed call would, so callers
            // never see an exception escape ForwardAsync.
            return LogAndError(toolName, ex);
        }

        try
        {
            return await client.CallToolAsync(toolName, args, null, null, cancellationToken);
        }
        catch (Exception ex) when (IsSessionNotFound(ex))
        {
            // almcp's HTTP transport evicts idle sessions (default 2h) and answers the stale
            // Mcp-Session-Id with 404. The request was never dispatched, so one retry on a fresh
            // session is safe even for non-idempotent tools like al_build.
            _logger.LogInformation("almcp session expired; reconnecting and retrying '{Tool}'", toolName);

            try
            {
                client = await ReplaceClientAsync(client, cancellationToken);
                return await client.CallToolAsync(toolName, args, null, null, cancellationToken);
            }
            catch (Exception retryEx)
            {
                await InvalidateClientAsync(client);
                return LogAndError(toolName, retryEx);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Caller cancelled; the session is still fine.
        }
        catch (Exception ex)
        {
            // Could have reached the server — never retry. Drop the client so the next call reconnects.
            await InvalidateClientAsync(client);
            return LogAndError(toolName, ex);
        }
    }

    private CallToolResult LogAndError(string toolName, Exception ex)
    {
        _logger.LogWarning(ex, "Error forwarding tool call '{Tool}' to almcp", toolName);
        return ErrorResult($"Error calling MS AL MCP tool '{toolName}': {ex.Message}");
    }

    /// <summary>
    /// A non-2xx POST surfaces as a plain <see cref="HttpRequestException"/> carrying the status
    /// code (the SDK's <c>EnsureSuccessStatusCodeWithResponseBodyAsync</c>), and <c>McpSession</c>
    /// re-throws it untouched — but walk the inner chain anyway so wrapping cannot silently turn a
    /// recoverable session loss into a hard failure.
    /// </summary>
    private static bool IsSessionNotFound(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
                return true;
        }

        return false;
    }

    private async Task<McpClient> GetOrCreateClientAsync(CancellationToken cancellationToken)
    {
        // Hot path: a plain read, so concurrent forwards never contend on the gate. McpClient is
        // safe for concurrent requests; only (re)creation needs serialising.
        var client = _client;
        if (client is not null)
            return client;

        await _clientGate.WaitAsync(cancellationToken);
        try
        {
            return _client ??= await CreateHttpClient(cancellationToken);
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private async Task<McpClient> ReplaceClientAsync(McpClient stale, CancellationToken cancellationToken)
    {
        await _clientGate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have already reconnected after hitting the same dead session.
            if (!ReferenceEquals(_client, stale))
                return _client ??= await CreateHttpClient(cancellationToken);

            _client = null;
            await DisposeClientAsync(stale);
            return _client = await CreateHttpClient(cancellationToken);
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private async Task InvalidateClientAsync(McpClient failed)
    {
        await _clientGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_client, failed))
                return;

            _client = null;
        }
        finally
        {
            _clientGate.Release();
        }

        await DisposeClientAsync(failed);
    }

    private async Task DisposeClientAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Expected when the session is already gone: the teardown DELETE 404s too.
            _logger.LogDebug(ex, "Error disposing almcp client");
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
        // Before the child is killed, so the session ends with a DELETE rather than by process death.
        if (_client is not null)
        {
            await DisposeClientAsync(_client);
            _client = null;
        }

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

        _clientGate.Dispose();
    }
}
