using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.Persistence;
using Npgsql;

namespace CrmAi.Tests;

[CollectionDefinition("Risk integration", DisableParallelization = true)]
public sealed class RiskIntegrationCollection { }

public sealed class RiskPostgresFactAttribute : FactAttribute
{
    public RiskPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RISK_ANALYSIS_TEST_DATABASE")))
            Skip="Requires isolated risk_delay_test PostgreSQL.";
    }
}

[Collection("Risk integration")]
public sealed class RiskAnalysisStateTests : IAsyncLifetime
{
    private NpgsqlDataSource _db=null!;
    private PostgresRiskAnalysisState _state=null!;
    private readonly CancellationToken _ct=CancellationToken.None;
    public async Task InitializeAsync()
    {
        var connection=Environment.GetEnvironmentVariable("RISK_ANALYSIS_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connection)) return;
        if (new NpgsqlConnectionStringBuilder(connection).Database!="risk_delay_test") throw new InvalidOperationException("Not an isolated test database.");
        _db=NpgsqlDataSource.Create(connection);
        _state=new(_db);
        await _state.InitializeAsync(_ct);
        await Sql("truncate risk_analysis_event_receipts,risk_analysis_state");
    }
    public async Task DisposeAsync() { if (_db is not null) await _db.DisposeAsync(); }
    internal static RiskAnalysisRequest Request(OpportunityAnalysisContext c, int minutes=1) =>
        new(Guid.Parse(c.Opportunity.CompanyId!),Guid.Parse(c.Opportunity.Id),c.TriggerEvent,minutes);
    private async Task Due() => await Sql("update risk_analysis_state set due_at=now()-interval '1 second'");
    private async Task Sql(string sql) { await using var command=_db.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private async Task<T> Scalar<T>(string sql) { await using var command=_db.CreateCommand(sql); return (T)(await command.ExecuteScalarAsync())!; }

    [RiskPostgresFact]
    public async Task NewEventsRestartDelayAndOlderVersionsNeverExecute()
    {
        var c=Context();
        var first=(await _state.RegisterAsync(Request(c,3),_ct))!;
        Assert.InRange((first.NotBeforeUtc-DateTime.UtcNow).TotalSeconds,170,180);
        await Sql("update risk_analysis_state set due_at=now()+interval '2 seconds'");
        var second=(await _state.RegisterAsync(Request(NewEvent(c),3),_ct))!;
        Assert.InRange((second.NotBeforeUtc-DateTime.UtcNow).TotalSeconds,170,180);
        Assert.Equal(RiskClaimStatus.Obsolete,(await _state.AcquireAsync(first,_ct)).Status);
        Assert.Equal(RiskClaimStatus.Wait,(await _state.AcquireAsync(second,_ct)).Status);
        await Due();
        var claim=(await _state.AcquireAsync(second,_ct)).Claim!;
        await _state.CompleteAsync(claim,null,_ct);
        for(var i=0;i<5;i++) Assert.Equal(RiskClaimStatus.Obsolete,(await _state.AcquireAsync(second,_ct)).Status);
    }
    [RiskPostgresFact]
    public async Task RedeliveryRecoversPublicationGapWithSameVersionAndDeadline()
    {
        var c=Context();
        var first=await _state.RegisterAsync(Request(c),_ct);
        var restarted=new PostgresRiskAnalysisState(_db);
        var replay=await restarted.RegisterAsync(Request(c),_ct);
        Assert.Equal(first,replay);
        var newer=await _state.RegisterAsync(Request(NewEvent(c)),_ct);
        Assert.Null(await _state.RegisterAsync(Request(c),_ct));
        Assert.Equal(2L,await Scalar<long>("select revision from risk_analysis_state"));
        Assert.NotNull(newer);
    }
    [RiskPostgresFact]
    public async Task UpdateDuringExecutionSurvivesOldCompletionAndWaitsAgain()
    {
        var c=Context();
        var first=(await _state.RegisterAsync(Request(c),_ct))!;
        await Due();
        var claim=(await _state.AcquireAsync(first,_ct)).Claim!;
        var second=(await _state.RegisterAsync(Request(NewEvent(c)),_ct))!;
        await _state.CompleteAsync(claim,null,_ct);
        Assert.Equal(RiskClaimStatus.Wait,(await _state.AcquireAsync(second,_ct)).Status);
        await Due();
        var secondClaim=(await _state.AcquireAsync(second,_ct)).Claim!;
        await _state.CompleteAsync(claim,null,_ct);
        Assert.Equal(RiskClaimStatus.Wait,(await _state.AcquireAsync(second,_ct)).Status);
        await _state.CompleteAsync(secondClaim,null,_ct);
        Assert.Equal(RiskClaimStatus.Obsolete,(await _state.AcquireAsync(second,_ct)).Status);
    }
    [RiskPostgresFact]
    public async Task ParallelEventsCoalesceAndOnlyOneExecutorAcquiresLatestVersion()
    {
        var c=Context();
        var works=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>_state.RegisterAsync(Request(NewEvent(c)),_ct)));
        Assert.Equal(12L,await Scalar<long>("select revision from risk_analysis_state"));
        var latest=works.OrderByDescending(w=>w!.Revision).First()!;
        await Due();
        var claims=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>_state.AcquireAsync(latest,_ct)));
        Assert.Single(claims,c=>c.Status==RiskClaimStatus.Acquired);
    }
    [RiskPostgresFact]
    public async Task StateSeparatesTenantsAndOpportunities()
    {
        var a=Context();var b=Context();var c=Context();
        c=c with { Opportunity=c.Opportunity with { CompanyId=a.Opportunity.CompanyId } };
        var wa=(await _state.RegisterAsync(Request(a),_ct))!;
        var wb=(await _state.RegisterAsync(Request(b),_ct))!;
        var wc=(await _state.RegisterAsync(Request(c),_ct))!;
        await Due();
        Assert.Equal(RiskClaimStatus.Obsolete,(await _state.AcquireAsync(wa with { CompanyId=wb.CompanyId },_ct)).Status);
        foreach(var w in new[]{wa,wb,wc}) Assert.Equal(RiskClaimStatus.Acquired,(await _state.AcquireAsync(w,_ct)).Status);
    }
    [RiskPostgresFact]
    public async Task ExpiredLeaseCanBeRecoveredButStaleWorkerCannotCompleteIt()
    {
        var w=(await _state.RegisterAsync(Request(Context()),_ct))!;
        await Due();var first=(await _state.AcquireAsync(w,_ct)).Claim!;
        await Sql("update risk_analysis_state set lease_until=now()-interval '1 second'");
        var second=(await _state.AcquireAsync(w,_ct)).Claim!;
        await _state.CompleteAsync(first,null,_ct);
        Assert.Equal(RiskClaimStatus.Wait,(await _state.AcquireAsync(w,_ct)).Status);
        await _state.CompleteAsync(second,null,_ct);
        Assert.Equal(RiskClaimStatus.Obsolete,(await _state.AcquireAsync(w,_ct)).Status);
    }
    [RiskPostgresFact]
    public async Task RetryLimitIsSharedAcrossDuplicateDeliveriesAndNewUpdateRemainsEligible()
    {
        var c=Context();var w=(await _state.RegisterAsync(Request(c),_ct))!;
        for(var i=0;i<3;i++)
        {
            await Due();var acquired=await _state.AcquireAsync(w,_ct);
            Assert.Equal(i+1,acquired.Attempts);
            await _state.RetryAsync(acquired.Claim!,"temporary failure",_ct);
            Assert.Equal(RiskClaimStatus.Wait,(await _state.AcquireAsync(w,_ct)).Status);
        }
        await Due();Assert.Equal(RiskClaimStatus.Exhausted,(await _state.AcquireAsync(w,_ct)).Status);
        var next=(await _state.RegisterAsync(Request(NewEvent(c)),_ct))!;
        await Due();Assert.Equal(RiskClaimStatus.Acquired,(await _state.AcquireAsync(next,_ct)).Status);
    }
    internal static OpportunityAnalysisContext NewEvent(OpportunityAnalysisContext c) => c with
    { TriggerEvent = c.TriggerEvent with { EventId = Guid.NewGuid().ToString(), OccurredAt = DateTime.UtcNow } };
    internal static OpportunityAnalysisContext Context(string status = "active")
    {
        var ev = new OpportunityEvent(Guid.NewGuid().ToString(), "opportunity.updated", DateTime.UtcNow,
            Guid.NewGuid().ToString(), null, new Dictionary<string, object?>());
        var stage = new PipelineStageSnapshot(Guid.NewGuid().ToString(), "Negotiation", 1);
        return new(new OpportunitySnapshot(ev.OpportunityId, Guid.NewGuid().ToString(), "Test", Guid.NewGuid().ToString(),
            stage.Id, null, null, 100, status, false, DateTime.UtcNow, DateTime.UtcNow, null), stage,
            [], [], [], [], [], null, [], [], [], ev);
    }
    internal sealed class Settings : IAiAgentRuntimeSettingsRepository
    {
        public int Minutes = 1;
        public bool Active = true;
        public Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken ct) =>
            Task.FromResult(new AiAgentRuntimeSettings(agentKey, Active, "OpenAI", "test", null, "", Minutes, null, []));
    }
}
