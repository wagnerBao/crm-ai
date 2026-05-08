using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.OpportunityAnalysis;

public sealed class RabbitMqOpportunityAnalysisConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqOpportunityAnalysisConsumer> logger)
    : RabbitMqOpportunityEventConsumerBase(scopeFactory, options, logger)
{
    protected override string ConnectionName => "crm-ai-opportunity-analysis";
    protected override string QueueName(RabbitMqOptions options) => options.OpportunityAnalysisQueue;
    protected override IReadOnlyCollection<string> ExchangeNames(RabbitMqOptions options) => options.OpportunityAnalysisExchangeNames;

    protected override Task ProcessAsync(IServiceProvider services, OpportunityEvent opportunityEvent, CancellationToken cancellationToken) =>
        services.GetRequiredService<IOpportunityAnalysisEventProcessor>().ProcessAsync(opportunityEvent, cancellationToken);
}
