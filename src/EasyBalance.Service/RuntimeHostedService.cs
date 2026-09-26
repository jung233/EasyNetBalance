using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EasyBalance.Service;

public sealed class RuntimeHostedService(
    EasyBalanceRuntime runtime,
    HealthCoordinator health,
    ILogger<RuntimeHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runtime.InitializeAsync(stoppingToken);
            await health.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogCritical(exception, "EasyBalance runtime stopped unexpectedly"); throw; }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await runtime.ShutdownAsync(cancellationToken);
    }
}
