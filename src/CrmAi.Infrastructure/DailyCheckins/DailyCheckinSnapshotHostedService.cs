using CrmAi.Application;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.DailyCheckins;

public sealed class DailyCheckinSnapshotHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<DailyCheckinSnapshotHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Clamp(options.Value.DailyCheckinSnapshotIntervalMinutes, 1, 1440);
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        await GenerateTodayAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await GenerateTodayAsync(stoppingToken);
        }
    }

    private async Task GenerateTodayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDailyCheckinProjectionService>();
            await service.GenerateDailySnapshotsAsync(DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to generate scheduled daily check-in snapshots.");
        }
    }
}
