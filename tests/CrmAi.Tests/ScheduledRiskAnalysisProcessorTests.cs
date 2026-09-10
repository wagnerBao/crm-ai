using CrmAi.Application;
using CrmAi.Domain;

namespace CrmAi.Tests;

public sealed class ScheduledRiskAnalysisProcessorTests
{
    [Theory]
    [InlineData("active", true, false, 1)]
    [InlineData("won", true, false, 0)]
    [InlineData("lost", true, false, 0)]
    [InlineData("active", false, false, 0)]
    [InlineData("active", true, true, 0)]
    public async Task UsesCurrentContextAndRespectsCompanyStatusAndAgent(string status, bool active, bool wrongCompany, int expected)
    {
        var context = RiskAnalysisStateTests.Context(status);
        var repository = new Repository(context);
        var agent = new Agent();
        var store = new Store();
        var processor = new ScheduledRiskAnalysisProcessor(repository, new RiskAnalysisStateTests.Settings { Active = active }, agent, store);
        var ev = context.TriggerEvent with { OccurredAt = DateTime.UtcNow.AddHours(-3) };
        var claim = new RiskAnalysisClaim(wrongCompany ? Guid.NewGuid() : Guid.Parse(context.Opportunity.CompanyId!),
            Guid.Parse(context.Opportunity.Id), Guid.NewGuid(), 1, ev);
        await processor.ProcessAsync(claim, CancellationToken.None);
        Assert.Equal(1, repository.Calls);
        Assert.Same(ev, repository.LastEvent);
        Assert.Equal(expected, agent.Calls);
        Assert.Equal(expected, store.Calls);
        if (expected == 1)
        {
            Assert.Same(context.Opportunity, agent.Context!.Opportunity);
            Assert.True(agent.Context.TriggerEvent.OccurredAt > ev.OccurredAt);
        }
    }
    private sealed class Repository(OpportunityAnalysisContext context) : IOpportunityContextRepository
    {
        public int Calls;
        public OpportunityEvent? LastEvent;
        public Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent ev, CancellationToken ct)
        { Calls++; LastEvent = ev; return Task.FromResult<OpportunityAnalysisContext?>(context); }
    }
    private sealed class Agent : IRiskAnalysisAgent
    {
        public int Calls;
        public OpportunityAnalysisContext? Context;
        public Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken ct)
        { Calls++; Context = context; return Task.FromResult(new RiskAnalysisResult(RiskLevel.Low, 0, [], [],
            new OpportunityAnalysisSnapshotUpdate(DateTime.UtcNow, 0, 0, 0, 0, null, 100, 100))); }
    }
    private sealed class Store : IAnalysisResultStore
    {
        public int Calls;
        public Task SaveRiskAnalysisAsync(OpportunityAnalysisContext context, RiskAnalysisResult result, CancellationToken ct)
        { Calls++; return Task.CompletedTask; }
    }
}
