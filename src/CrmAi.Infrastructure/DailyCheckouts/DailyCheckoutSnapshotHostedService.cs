using CrmAi.Application;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.DailyCheckouts;

public sealed class DailyCheckoutSnapshotHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<DailyCheckoutSnapshotHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Clamp(options.Value.DailyCheckoutSnapshotIntervalMinutes, 1, 1440);
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        await GenerateDueAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await GenerateDueAsync(stoppingToken);
        }
    }

    private async Task GenerateDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDailyCheckoutSnapshotService>();
            await service.GenerateDueSnapshotsAsync(DateTime.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to generate scheduled daily checkout snapshots.");
        }
    }
}

