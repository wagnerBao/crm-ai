using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.DailyCheckins;

public sealed class RabbitMqDailyCheckinConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqDailyCheckinConsumer> logger)
    : RabbitMqOpportunityEventConsumerBase(scopeFactory, options, logger)
{
    protected override string ConnectionName => "crm-ai-daily-checkin";
    protected override string QueueName(RabbitMqOptions options) => options.DailyCheckinQueue;
    protected override IReadOnlyCollection<string> ExchangeNames(RabbitMqOptions options) => options.DailyCheckinExchangeNames;

    protected override Task ProcessAsync(IServiceProvider services, OpportunityEvent opportunityEvent, CancellationToken cancellationToken) =>
        services.GetRequiredService<IDailyCheckinProjectionService>().ProjectAsync(opportunityEvent, cancellationToken);
}
