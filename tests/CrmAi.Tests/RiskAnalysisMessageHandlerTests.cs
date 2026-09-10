using CrmAi.Application;
using CrmAi.Domain;

namespace CrmAi.Tests;

public sealed class RiskAnalysisMessageHandlerTests
{
    [Fact]
    public async Task WaitingDeliveryStaysInRabbitWithoutQueryingStateOrCallingAi()
    {
        var (handler,state,messages,agent,work)=Fixture();
        await handler.ExecuteAsync(work with { NotBeforeUtc=DateTime.UtcNow.AddMinutes(1) },default);
        Assert.Equal(0,state.Acquisitions);Assert.Equal(0,agent.Calls);Assert.Equal(1,messages.Works);
    }
    [Theory]
    [InlineData(RiskClaimStatus.Obsolete,0,0)]
    [InlineData(RiskClaimStatus.Wait,1,0)]
    [InlineData(RiskClaimStatus.Exhausted,0,1)]
    public async Task ObsoleteBusyAndExhaustedDeliveriesNeverCallAi(RiskClaimStatus status,int works,int failures)
    {
        var (handler,state,messages,agent,work)=Fixture();
        state.Status=status;
        await handler.ExecuteAsync(work,default);
        Assert.Equal(0,agent.Calls);Assert.Equal(works,messages.Works);Assert.Equal(failures,messages.Failures);
    }
    [Theory]
    [InlineData(503,"temporary",1,true)]
    [InlineData(429,"rate_limit_exceeded",1,true)]
    [InlineData(429,"insufficient_quota",1,false)]
    [InlineData(429,"credit_balance_exhausted",1,false)]
    [InlineData(400,"invalid_request",1,false)]
    [InlineData(503,"temporary",3,false)]
    public async Task RetryPolicyPreservesFailedWorkAndBoundsProviderCalls(int status,string body,int attempts,bool retry)
    {
        var (handler,state,messages,agent,work)=Fixture();
        state.Attempts=attempts;agent.Error=new OpenAiRequestException("test error",status,body);
        await handler.ExecuteAsync(work,default);
        Assert.Equal(1,agent.Calls);Assert.Equal(retry?1:0,state.Retries);
        Assert.Equal(retry?1:0,messages.Works);Assert.Equal(retry?0:1,messages.Failures);
        Assert.Equal(retry?0:1,state.Completions);
    }
    [Fact]
    public async Task FailedPublicationPropagatesSoTheConsumerCannotAcknowledgeTheRequest()
    {
        var (handler,state,messages,_,work)=Fixture();messages.ThrowOnPublish=true;
        await Assert.ThrowsAsync<IOException>(()=>handler.ScheduleAsync(new(work.CompanyId,work.OpportunityId,work.Event,1),default));
        Assert.Equal(1,state.Registrations);
    }
    [Fact]
    public async Task SchedulingPublishesOnlyIdentifiersAndHonorsConfiguredDelayAndEnabledFlag()
    {
        var settings=new RiskAnalysisStateTests.Settings { Minutes=7 };
        var messages=new Messages();var scheduler=new RabbitRiskAnalysisScheduler(settings,messages);
        var c=RiskAnalysisStateTests.Context();
        c=c with { TriggerEvent=c.TriggerEvent with { Data=new Dictionary<string,object?> { ["text"]="large transcript" } } };
        await scheduler.ScheduleAsync(c,default);
        Assert.Equal(7,messages.Request!.DelayMinutes);Assert.Empty(messages.Request.Event.Data);
        Assert.Equal(c.TriggerEvent.EventId,messages.Request.Event.EventId);
        settings.Active=false;messages.Request=null;
        await scheduler.ScheduleAsync(c,default);Assert.Null(messages.Request);
    }
    private static (RiskAnalysisMessageHandler,State,Messages,Agent,RiskAnalysisWork) Fixture()
    {
        var c=RiskAnalysisStateTests.Context();var agent=new Agent();var messages=new Messages();
        var work=new RiskAnalysisWork(Guid.Parse(c.Opportunity.CompanyId!),Guid.Parse(c.Opportunity.Id),1,c.TriggerEvent,DateTime.UtcNow.AddMinutes(-1));
        var state=new State(work);
        var processor=new ScheduledRiskAnalysisProcessor(new Repository(c),new RiskAnalysisStateTests.Settings(),agent,new Store());
        return (new(state,messages,processor),state,messages,agent,work);
    }
    private sealed class State(RiskAnalysisWork work):IRiskAnalysisState
    {
        public int Acquisitions,Registrations,Retries,Completions;
        public int Attempts=1;public RiskClaimStatus Status=RiskClaimStatus.Acquired;
        public Task InitializeAsync(CancellationToken ct)=>Task.CompletedTask;
        public Task<RiskAnalysisWork?> RegisterAsync(RiskAnalysisRequest request,CancellationToken ct)
        { Registrations++;return Task.FromResult<RiskAnalysisWork?>(work); }
        public Task<RiskClaimResult> AcquireAsync(RiskAnalysisWork w,CancellationToken ct)
        { Acquisitions++;return Task.FromResult(new RiskClaimResult(Status,new(w.CompanyId,w.OpportunityId,Guid.NewGuid(),w.Revision,w.Event),DateTime.UtcNow.AddSeconds(10),Attempts)); }
        public Task CompleteAsync(RiskAnalysisClaim c,string? e,CancellationToken ct){Completions++;return Task.CompletedTask;}
        public Task RetryAsync(RiskAnalysisClaim c,string e,CancellationToken ct){Retries++;return Task.CompletedTask;}
    }
    private sealed class Messages:IRiskAnalysisMessages
    {
        public int Works,Failures;public bool ThrowOnPublish;public RiskAnalysisRequest? Request;
        public void PublishRequest(RiskAnalysisRequest r)=>Request=r;
        public void PublishWork(RiskAnalysisWork w){if(ThrowOnPublish)throw new IOException("broker unavailable");Works++;}
        public void PublishFailure(RiskAnalysisWork w,string error)=>Failures++;
    }
    private sealed class Repository(OpportunityAnalysisContext c):IOpportunityContextRepository
    { public Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent ev,CancellationToken ct)=>Task.FromResult<OpportunityAnalysisContext?>(c); }
    private sealed class Agent:IRiskAnalysisAgent
    {
        public int Calls;public Exception? Error;
        public Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext c,CancellationToken ct)
        { Calls++;if(Error is not null)throw Error;return Task.FromResult(new RiskAnalysisResult(RiskLevel.Low,0,[],[],new(DateTime.UtcNow,0,0,0,0,null,100,100))); }
    }
    private sealed class Store:IAnalysisResultStore
    { public Task SaveRiskAnalysisAsync(OpportunityAnalysisContext c,RiskAnalysisResult result,CancellationToken ct)=>Task.CompletedTask; }
}
