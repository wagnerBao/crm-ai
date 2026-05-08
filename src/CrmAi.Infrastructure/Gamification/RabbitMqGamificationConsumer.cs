using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.Gamification;

public sealed class RabbitMqGamificationConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqGamificationConsumer> logger)
    : RabbitMqOpportunityEventConsumerBase(scopeFactory, options, logger)
{
    protected override string ConnectionName => "crm-ai-gamification";
    protected override string QueueName(RabbitMqOptions options) => options.GamificationQueue;
    protected override IReadOnlyCollection<string> ExchangeNames(RabbitMqOptions options) => options.GamificationExchangeNames;

    protected override Task ProcessAsync(IServiceProvider services, OpportunityEvent opportunityEvent, CancellationToken cancellationToken) =>
        services.GetRequiredService<IGamificationProjectionService>().ProjectAsync(opportunityEvent, cancellationToken);
}
