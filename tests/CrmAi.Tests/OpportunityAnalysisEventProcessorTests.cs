using CrmAi.Application;
using CrmAi.Domain;

namespace CrmAi.Tests;

public sealed class OpportunityAnalysisEventProcessorTests
{
    [Theory]
    [InlineData("opportunity.activity.created")]
    [InlineData("opportunity.activity.updated")]
    [InlineData("activity.created")]
    [InlineData("activity.updated")]
    public async Task ProcessAsync_DoesNotRunOpportunityRiskAnalysis_ForActivityEvents(string eventType)
    {
        var contextRepository = new CountingOpportunityContextRepository();
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var processor = new OpportunityAnalysisEventProcessor(
            contextRepository,
            riskAgent,
            resultStore,
            new NullWhatsappConversationAnalysisAgent(),
            new NullWhatsappConversationActionStore(),
            new NullWhatsappConversationAnalysisScheduler(),
            new NullMeetingAudioAnalysisService());

        await processor.ProcessAsync(
            new OpportunityEvent(
                Guid.NewGuid().ToString(),
                eventType,
                DateTime.UtcNow,
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                new Dictionary<string, object?>()),
            CancellationToken.None);

        Assert.Equal(0, contextRepository.Calls);
        Assert.Equal(0, riskAgent.Calls);
        Assert.Equal(0, resultStore.Calls);
    }

    private sealed class CountingOpportunityContextRepository : IOpportunityContextRepository
    {
        public int Calls { get; private set; }

        public Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent triggerEvent, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<OpportunityAnalysisContext?>(null);
        }
    }

    private sealed class CountingRiskAnalysisAgent : IRiskAnalysisAgent
    {
        public int Calls { get; private set; }

        public Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new RiskAnalysisResult(
                RiskLevel.Low,
                0,
                [],
                [],
                new OpportunityAnalysisSnapshotUpdate(DateTime.UtcNow, 0, 0, 0, 0, null, 100, 100)));
        }
    }

    private sealed class CountingAnalysisResultStore : IAnalysisResultStore
    {
        public int Calls { get; private set; }

        public Task SaveRiskAnalysisAsync(OpportunityAnalysisContext context, RiskAnalysisResult result, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class NullWhatsappConversationAnalysisAgent : IWhatsappConversationAnalysisAgent
    {
        public Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken) =>
            Task.FromResult<WhatsappConversationAnalysisResult?>(null);
    }

    private sealed class NullWhatsappConversationActionStore : IWhatsappConversationActionStore
    {
        public Task ApplyAsync(OpportunityAnalysisContext context, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullWhatsappConversationAnalysisScheduler : IWhatsappConversationAnalysisScheduler
    {
        public Task ScheduleAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyCollection<OpportunityEvent>> ClaimDueAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<OpportunityEvent>>([]);

        public Task CompleteAsync(string eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FailAsync(string eventId, string error, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullMeetingAudioAnalysisService : IMeetingAudioAnalysisService
    {
        public Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
