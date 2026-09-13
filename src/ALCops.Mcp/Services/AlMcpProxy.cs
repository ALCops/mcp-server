using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

    // Startup runs on a background task (see AlMcpProxyStartup), so every consumer needs a way to
    // find out whether almcp will ever be usable without blocking the MCP server's own startup.
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _toolListChangedArmed;

    public bool IsAvailable { get; }
    public bool IsStarted => _childProcess is not null && !_childProcess.HasExited;

    /// <summary>Completes true once almcp is up and its tools are cached; false if it never will be.</summary>
    public Task<bool> Ready => _ready.Task;

    public bool IsReady => Ready is { IsCompletedSuccessfully: true, Result: true };

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
        {
            _logger.LogInformation("Found almcp at: {Path}", _almcpPath);
        }
        else
        {
            // Nothing will ever start it, so settle Ready now rather than leave waiters parked.
            _ready.TrySetResult(false);
            _logger.LogWarning("almcp not found at {Path}. MS AL MCP tools will be unavailable.", _almcpPath);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return;

        try
        {
            await StartCoreAsync(cancellationToken);
            _ready.TrySetResult(true);
        }
        catch
        {
            _ready.TrySetResult(false);
            throw;
        }
    }

    // FindFreePort has to release the port before almcp can bind it, so another process can take it
    // in between. Rare, but on a busy CI box it happens; a fresh port on the next attempt is the fix.
    private const int MaxStartAttempts = 3;

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var resolverArgs = await _workspaceResolver.BuildAlMcpArgsAsync(_passthroughArgs);

        for (var attempt = 1; ; attempt++)
        {
            _port = FindFreePort();
            LaunchChild(["--port", _port.ToString(), .. resolverArgs]);

            try
            {
                await WaitForServerReady(cancellationToken);
                break;
            }
            catch (InvalidOperationException ex) when (attempt < MaxStartAttempts && _childProcess is { HasExited: true })
            {
                // Only an early *exit* is retried — almcp dies on "address already in use". A child
                // that is alive but serves the wrong thing is a different problem and fails outright.
                _logger.LogWarning(
                    "almcp exited during startup on port {Port} (attempt {Attempt}/{Max}): {Message} Retrying on a new port.",
                    _port, attempt, MaxStartAttempts, ex.Message);
                _childProcess?.Dispose();
                _childProcess = null;
            }
        }

        // Through the gate, not by assignment: startup now runs concurrently with requests, and an
        // early ForwardAsync would otherwise create a second client for the same child.
        var client = await GetOrCreateClientAsync(cancellationToken);
        _cachedTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        _logger.LogInformation("Discovered {Count} tools from almcp", _cachedTools.Count);
    }

    private void LaunchChild(string[] args)
    {
        _logger.LogInformation("Starting almcp on port {Port}: {Path} {Args}", _port, _almcpPath, string.Join(' ', args));

        // Both streams must be captured. In HTTP mode almcp writes its banner, "Port: N" and the
        // project-load progress to *stdout* (Program.cs passes Console.WriteLine as the output
        // sink); left uncaptured, the child inherits our stdout and that text lands in the middle
        // of the MCP JSON-RPC stream.
        var psi = new ProcessStartInfo
        {
            FileName = _almcpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _childProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start almcp process.");

        _childProcess.OutputDataReceived += (_, e) => LogChildLine(e.Data);
        _childProcess.ErrorDataReceived += (_, e) => LogChildLine(e.Data);
        _childProcess.BeginOutputReadLine();
        _childProcess.BeginErrorReadLine();
    }

    private void LogChildLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            _logger.LogDebug("[almcp] {Line}", line);
    }

    public IList<McpClientTool> GetCachedTools() => _cachedTools ?? [];

    public async Task<CallToolResult> ForwardAsync(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        // Calls that arrive while almcp is still booting simply wait for it; the caller's token and
        // almcp's own readiness timeout bound the wait.
        if (!await Ready.WaitAsync(cancellationToken))
            return ErrorResult("MS AL MCP Server is not available (almcp did not start).");

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

    /// <summary>
    /// Called by the tools/list handler when it had to answer before almcp was ready: tells that
    /// session to re-list once we are. One shot — a second stale list must not queue a second ping.
    /// </summary>
    public void NotifyToolListChangedWhenReady(McpServer server)
    {
        if (Interlocked.Exchange(ref _toolListChangedArmed, 1) == 1)
            return;

        _ = SendToolListChangedWhenReadyAsync(server);
    }

    private async Task SendToolListChangedWhenReadyAsync(McpServer server)
    {
        if (!await Ready)
            return;

        try
        {
            await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification);
        }
        catch (Exception ex)
        {
            // The session may be gone by the time almcp comes up; nothing to recover.
            _logger.LogDebug(ex, "Could not send tools/list_changed");
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
    /// root — not <c>/mcp/</c>, which 404s. It binds <c>localhost</c>, i.e. both loopback families;
    /// we connect to the IPv4 one <see cref="FindFreePort"/> reserved rather than resolving
    /// <c>localhost</c> per call and possibly trying <c>::1</c> first.
    /// </summary>
    private Uri McpEndpoint => new($"http://127.0.0.1:{_port}/");

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
        // Releases anything still parked in ForwardAsync waiting for a child that is going away.
        _ready.TrySetResult(false);

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
