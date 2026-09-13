using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ALCops.Mcp.Services;

internal sealed class AlcopsAnalyzerProvisionerStartup : IHostedService
{
    private readonly AlcopsAnalyzerProvisioner _provisioner;
    private readonly ILogger<AlcopsAnalyzerProvisionerStartup> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _startup = Task.CompletedTask;

    public AlcopsAnalyzerProvisionerStartup(
        AlcopsAnalyzerProvisioner provisioner,
        ILogger<AlcopsAnalyzerProvisionerStartup> logger)
    {
        _provisioner = provisioner;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startup = Task.Run(() => RunStartupAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _provisioner.ProvisionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ALCops analyzer provisioning failed");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        await _startup;
        _cts.Dispose();
    }
}
