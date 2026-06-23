using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.OpportunityAnalysis;

public sealed class RabbitMqActivityAnalysisConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqActivityAnalysisConsumer> logger)
    : RabbitMqOpportunityEventConsumerBase(scopeFactory, options, logger)
{
    protected override string ConnectionName => "crm-ai-activity-analysis";
    protected override string QueueName(RabbitMqOptions options) => options.ActivityAnalysisQueue;
    protected override IReadOnlyCollection<string> ExchangeNames(RabbitMqOptions options) => options.ActivityAnalysisExchangeNames;
    protected override bool RequiresOpportunityId => false;

    protected override Task ProcessAsync(IServiceProvider services, OpportunityEvent opportunityEvent, CancellationToken cancellationToken) =>
        services.GetRequiredService<IActivityAnalysisEventProcessor>().ProcessAsync(opportunityEvent, cancellationToken);
}
