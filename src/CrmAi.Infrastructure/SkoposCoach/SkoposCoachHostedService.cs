using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrmAi.Infrastructure.SkoposCoach;

public sealed class SkoposCoachHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SkoposCoachHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        await RunAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RunAsync(stoppingToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<SkoposCoachProjectionService>().ProjectAndProcessAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Skopos Coach projection cycle failed. Other AI agents remain isolated.");
        }
        try
        {
            await scope.ServiceProvider.GetRequiredService<SkoposIndividualCoachProcessor>().ProcessPendingAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Skopos Individual Coach cycle failed. The collective Coach remains isolated.");
        }
    }
}
