using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.OpportunityAnalysis;
using CrmAi.Infrastructure.Persistence;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;

namespace CrmAi.Tests;

public sealed class RiskRabbitFactAttribute : FactAttribute
{
    public RiskRabbitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RISK_ANALYSIS_TEST_DATABASE"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RISK_ANALYSIS_TEST_RABBITMQ")))
            Skip="Requires isolated PostgreSQL and RabbitMQ.";
    }
}

[Collection("Risk integration")]
public sealed class RabbitRiskAnalysisTests
{
    private static IOptions<RabbitMqOptions> OptionsForTest() => Options.Create(new RabbitMqOptions
    {
        Uri=Environment.GetEnvironmentVariable("RISK_ANALYSIS_TEST_RABBITMQ")!,
        RiskAnalysisQueue="tst.risk.integration."+Guid.NewGuid().ToString("N"),
        RiskAnalysisConsumerCount=2,RiskAnalysisSchedulerCount=1
    });
    private static NpgsqlDataSource Database()
    {
        var connection=Environment.GetEnvironmentVariable("RISK_ANALYSIS_TEST_DATABASE")!;
        if(new NpgsqlConnectionStringBuilder(connection).Database!="risk_delay_test") throw new InvalidOperationException("Not an isolated database.");
        return NpgsqlDataSource.Create(connection);
    }
    private static async Task WaitFor(Func<Task<bool>> condition,int seconds=15)
    {
        var until=DateTime.UtcNow.AddSeconds(seconds);
        while(DateTime.UtcNow<until) { if(await condition()) return; await Task.Delay(50); }
        Assert.Fail("Timed out waiting for asynchronous RabbitMQ processing.");
    }
    private static RiskAnalysisWork Work() { var c=RiskAnalysisStateTests.Context(); return new(Guid.Parse(c.Opportunity.CompanyId!),Guid.Parse(c.Opportunity.Id),1,c.TriggerEvent,DateTime.UtcNow); }

    [Theory]
    [InlineData(0.5,1)] [InlineData(59.9,60)] [InlineData(120,60)] [InlineData(1020,900)]
    public void FixedDelayQueuesUseBoundedBuckets(double seconds,int expected) =>
        Assert.Equal(expected,RabbitMqRiskAnalysisMessages.SelectDelay(TimeSpan.FromSeconds(seconds)));

    [RiskRabbitFact]
    public async Task BrokerHoldsDelayedWorkThenRedeliversItAfterConsumerDisconnects()
    {
        using var publisher=new RabbitMqRiskAnalysisMessages(OptionsForTest());
        var work=Work() with { NotBeforeUtc=DateTime.UtcNow.AddSeconds(2) };
        publisher.PublishWork(work);
        using var connection=publisher.CreateFactory().CreateConnection();
        using var channel=connection.CreateModel();
        Assert.Null(channel.BasicGet(publisher.ReadyQueue,false));
        // Re-entering shorter buckets is performed by the delivery handler, never a DB timer.
        BasicGetResult? delivery=null;
        await WaitFor(()=>Task.FromResult((delivery=channel.BasicGet(publisher.ReadyQueue,false)) is not null));
        Assert.True(delivery!.BasicProperties.Persistent);
        Assert.Equal(JsonSerializer.Serialize(work),JsonSerializer.Serialize(JsonSerializer.Deserialize<RiskAnalysisWork>(delivery.Body.ToArray())));
        channel.Close(); // No ACK: broker must retain and redeliver the work.
        using var replacement=connection.CreateModel();
        await WaitFor(()=>Task.FromResult((delivery=replacement.BasicGet(publisher.ReadyQueue,false)) is not null));
        Assert.True(delivery!.Redelivered);
        replacement.BasicAck(delivery.DeliveryTag,false);
        publisher.PublishFailure(work,"test failure");
        var failed=replacement.BasicGet(publisher.FailureQueue,true);
        Assert.NotNull(failed);
        Assert.True(failed!.BasicProperties.Headers.ContainsKey("x-risk-error"));
    }

    [RiskRabbitFact]
    public async Task BrokerRetainsExpiredDelayUntilDestinationReturns()
    {
        using var publisher=new RabbitMqRiskAnalysisMessages(OptionsForTest());
        using var connection=publisher.CreateFactory().CreateConnection();using var channel=connection.CreateModel();
        publisher.DeclareTopology(channel);
        channel.QueueDelete(publisher.ReadyQueue,false,false);
        var work=Work() with { NotBeforeUtc=DateTime.UtcNow.AddSeconds(2) };
        publisher.PublishWork(work); // Publisher creates its channel and restores topology first.
        channel.QueueDelete(publisher.ReadyQueue,false,false);
        await Task.Delay(1500); // TTL expires while the destination is unavailable.
        publisher.DeclareTopology(channel);
        BasicGetResult? delivery=null;
        // RabbitMQ 3.13 may retry a missing dead-letter destination after its default 180-second confirmation timeout.
        await WaitFor(()=>Task.FromResult((delivery=channel.BasicGet(publisher.ReadyQueue,true)) is not null),210);
        Assert.Equal(work.Revision,JsonSerializer.Deserialize<RiskAnalysisWork>(delivery!.Body.ToArray())!.Revision);
    }

    [RiskRabbitFact]
    public async Task LostPublisherConfirmationCanBeRecoveredWithoutLosingCommittedSchedule()
    {
        await using var db=Database();var state=new PostgresRiskAnalysisState(db);await state.InitializeAsync(default);
        var c=RiskAnalysisStateTests.Context();var request=RiskAnalysisStateTests.Request(c);
        var first=(await state.RegisterAsync(request,default))!;
        // Simulate process death after metadata commit, before publication.
        using var publisher=new RabbitMqRiskAnalysisMessages(OptionsForTest());
        var replay=(await new PostgresRiskAnalysisState(db).RegisterAsync(request,default))!;
        Assert.Equal(first,replay);
        publisher.PublishWork(replay with { NotBeforeUtc=DateTime.UtcNow });
        using var connection=publisher.CreateFactory().CreateConnection();using var channel=connection.CreateModel();
        var delivery=channel.BasicGet(publisher.ReadyQueue,true);
        Assert.NotNull(delivery);
    }

    [RiskRabbitFact]
    public async Task RealConsumersConsolidateAndExecuteOnce_ThenOnlyAfterAnotherUpdate()
    {
        await using var db=Database();var options=OptionsForTest();
        var context=RiskAnalysisStateTests.Context();
        var repository=new Repository(context);var agent=new CountingAgent();
        var services=new ServiceCollection();
        services.AddSingleton(db);services.AddSingleton(options);
        services.AddSingleton<RabbitMqRiskAnalysisMessages>();
        services.AddSingleton<IRiskAnalysisMessages>(sp=>sp.GetRequiredService<RabbitMqRiskAnalysisMessages>());
        services.AddScoped<IRiskAnalysisState,PostgresRiskAnalysisState>();
        services.AddSingleton<IAiAgentRuntimeSettingsRepository>(new RiskAnalysisStateTests.Settings());
        services.AddSingleton<IOpportunityContextRepository>(repository);
        services.AddSingleton<IRiskAnalysisAgent>(agent);services.AddSingleton<IAnalysisResultStore>(new Store());
        services.AddScoped<ScheduledRiskAnalysisProcessor>();services.AddScoped<RiskAnalysisMessageHandler>();
        await using var provider=services.BuildServiceProvider();
        var publisher=provider.GetRequiredService<RabbitMqRiskAnalysisMessages>();
        using var host=new RabbitMqRiskAnalysisHostedService(provider.GetRequiredService<IServiceScopeFactory>(),publisher,options,
            NullLogger<RabbitMqRiskAnalysisHostedService>.Instance);
        await host.StartAsync(default);
        try
        {
            publisher.PublishRequest(RiskAnalysisStateTests.Request(context));
            await WaitFor(async()=>await Revision(db,context)==1);
            var second=RiskAnalysisStateTests.NewEvent(context);
            publisher.PublishRequest(RiskAnalysisStateTests.Request(second));
            await WaitFor(async()=>await Revision(db,context)==2);
            using(var connection=publisher.CreateFactory().CreateConnection())
            using(var channel=connection.CreateModel())
            {
                await WaitFor(()=>Task.FromResult(channel.MessageCount(publisher.DelayQueue(60))>=2));
            }
            Assert.Equal(0,agent.Calls);
            // Expire this opportunity's deadline to avoid a one-minute test sleep. Broker TTL is tested separately.
            await Expire(db,context);
            var work=new RiskAnalysisWork(Guid.Parse(context.Opportunity.CompanyId!),Guid.Parse(context.Opportunity.Id),2,second.TriggerEvent,DateTime.UtcNow);
            publisher.PublishWork(work with { Revision=1,Event=context.TriggerEvent }); // Superseded: no AI.
            publisher.PublishWork(work);
            publisher.PublishWork(work); // Concurrent duplicate deliveries: one AI call.
            await WaitFor(async()=>await Processed(db,context)==2);
            Assert.Equal(1,agent.Calls);
            publisher.PublishWork(work); // Even after completion, a delayed duplicate must not execute.
            await Task.Delay(500);Assert.Equal(1,agent.Calls);
            var third=RiskAnalysisStateTests.NewEvent(context);
            publisher.PublishRequest(RiskAnalysisStateTests.Request(third));
            await WaitFor(async()=>await Revision(db,context)==3);
            Assert.Equal(1,agent.Calls);
            await Expire(db,context);
            publisher.PublishWork(work with { Revision=3,Event=third.TriggerEvent,NotBeforeUtc=DateTime.UtcNow });
            await WaitFor(async()=>await Processed(db,context)==3);
            Assert.Equal(2,agent.Calls);
        }
        finally { await host.StopAsync(default); }
    }
    private static async Task<long> Revision(NpgsqlDataSource db,OpportunityAnalysisContext c)=>await Read(db,c,"revision");
    private static async Task<long> Processed(NpgsqlDataSource db,OpportunityAnalysisContext c)=>await Read(db,c,"processed_revision");
    private static async Task<long> Read(NpgsqlDataSource db,OpportunityAnalysisContext c,string column)
    {
        await using var command=db.CreateCommand($"select {column} from risk_analysis_state where company_id=@company and opportunity_id=@opportunity");
        command.Parameters.AddWithValue("company",Guid.Parse(c.Opportunity.CompanyId!));command.Parameters.AddWithValue("opportunity",Guid.Parse(c.Opportunity.Id));
        return (long?)await command.ExecuteScalarAsync()??0;
    }
    private static async Task Expire(NpgsqlDataSource db,OpportunityAnalysisContext c)
    {
        await using var command=db.CreateCommand("update risk_analysis_state set due_at=now()-interval '1 second' where company_id=@company and opportunity_id=@opportunity");
        command.Parameters.AddWithValue("company",Guid.Parse(c.Opportunity.CompanyId!));command.Parameters.AddWithValue("opportunity",Guid.Parse(c.Opportunity.Id));
        await command.ExecuteNonQueryAsync();
    }
    private sealed class Repository(OpportunityAnalysisContext context):IOpportunityContextRepository
    { public Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent ev,CancellationToken ct)=>Task.FromResult<OpportunityAnalysisContext?>(context with { TriggerEvent=ev }); }
    private sealed class CountingAgent:IRiskAnalysisAgent
    {
        public int Calls;
        public Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext c,CancellationToken ct)
        { Interlocked.Increment(ref Calls);return Task.FromResult(new RiskAnalysisResult(RiskLevel.Low,0,[],[],new(DateTime.UtcNow,0,0,0,0,null,100,100))); }
    }
    private sealed class Store:IAnalysisResultStore
    { public Task SaveRiskAnalysisAsync(OpportunityAnalysisContext c,RiskAnalysisResult result,CancellationToken ct)=>Task.CompletedTask; }
}
