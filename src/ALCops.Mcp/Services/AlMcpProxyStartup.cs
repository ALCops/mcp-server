using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALCops.Mcp.Services;

internal sealed class AlMcpProxyStartup : IHostedService
{
    private readonly AlMcpProxy _proxy;
    private readonly ILogger<AlMcpProxyStartup> _logger;

    public AlMcpProxyStartup(AlMcpProxy proxy, ILogger<AlMcpProxyStartup> logger)
    {
        _proxy = proxy;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_proxy.IsAvailable)
        {
            _logger.LogInformation("almcp proxy disabled (executable not found).");
            return;
        }

        try
        {
            await _proxy.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start almcp proxy. MS AL MCP tools will be unavailable.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _proxy.DisposeAsync();
    }
}
