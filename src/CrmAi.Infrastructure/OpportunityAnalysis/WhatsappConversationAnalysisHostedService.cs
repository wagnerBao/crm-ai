using CrmAi.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrmAi.Infrastructure.OpportunityAnalysis;

public sealed class WhatsappConversationAnalysisHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<WhatsappConversationAnalysisHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessDueAsync(stoppingToken);
            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<IWhatsappConversationAnalysisScheduler>();
            var processor = scope.ServiceProvider.GetRequiredService<IOpportunityAnalysisEventProcessor>();
            var events = await scheduler.ClaimDueAsync(20, cancellationToken);

            foreach (var opportunityEvent in events)
            {
                try
                {
                    await processor.ProcessAsync(opportunityEvent, cancellationToken);
                    await scheduler.CompleteAsync(opportunityEvent.EventId, cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to process consolidated WhatsApp analysis event {EventId}.", opportunityEvent.EventId);
                    await scheduler.FailAsync(opportunityEvent.EventId, exception.Message, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to scan pending WhatsApp conversation analyses.");
        }
    }
}
