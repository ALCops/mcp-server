using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALCops.Mcp.Services;

internal sealed class AlMcpProxyStartup : IHostedService
{
    private readonly AlMcpProxy _proxy;
    private readonly ILogger<AlMcpProxyStartup> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _startup = Task.CompletedTask;

    public AlMcpProxyStartup(AlMcpProxy proxy, ILogger<AlMcpProxyStartup> logger)
    {
        _proxy = proxy;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_proxy.IsAvailable)
        {
            _logger.LogInformation("almcp proxy disabled (executable not found).");
            return Task.CompletedTask;
        }

        // Deliberately not awaited: the stdio MCP server is the next hosted service in line, and the
        // native tools must not wait for a child process that can take tens of seconds to load a
        // project. AlMcpProxy.Ready is how the handlers find out when almcp is usable.
        _startup = Task.Run(() => RunStartupAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _proxy.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown raced the boot; StopAsync is already tearing the child down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start almcp proxy. MS AL MCP tools will be unavailable.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        await _startup; // RunStartupAsync never throws.
        await _proxy.DisposeAsync();
        _cts.Dispose();
    }
}
