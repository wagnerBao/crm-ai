using CrmAi.Application;
using CrmAi.Domain;

namespace CrmAi.Tests;

public sealed class OpportunityAnalysisEventProcessorTests
{
    [Fact]
    public async Task ProcessAsync_SchedulesWhatsappConversationAnalysis_ForWhatsappMessageEvents()
    {
        var contextRepository = new CountingOpportunityContextRepository();
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var scheduler = new CountingWhatsappConversationAnalysisScheduler();
        var processor = new OpportunityAnalysisEventProcessor(
            contextRepository,
            riskAgent,
            resultStore,
            new NullWhatsappConversationAnalysisAgent(),
            new CountingWhatsappConversationActionStore(),
            scheduler,
            new NullMeetingAudioAnalysisService());
        var opportunityEvent = CreateEvent("opportunity.whatsapp.message.created");

        await processor.ProcessAsync(opportunityEvent, CancellationToken.None);

        Assert.Equal(1, scheduler.ScheduleCalls);
        Assert.Same(opportunityEvent, scheduler.LastScheduledEvent);
        Assert.Equal(0, contextRepository.Calls);
        Assert.Equal(0, riskAgent.Calls);
        Assert.Equal(0, resultStore.Calls);
    }

    [Fact]
    public async Task ProcessAsync_AppliesWhatsappResultAndRunsRiskAnalysis_ForWhatsappConversationBatchEvents()
    {
        var initialContext = CreateContext(CreateEvent("opportunity.whatsapp.conversation.batch"));
        var refreshedContext = initialContext with
        {
            Activities =
            [
                new ActivitySnapshot(Guid.NewGuid().ToString(), "Conversa WhatsApp analisada pelo Agente Skopos", "Agente Skopos", "whatsapp", "done", DateTime.UtcNow, "Resumo", "Resumo mais recente", null, DateTime.UtcNow, DateTime.UtcNow)
            ]
        };
        var contextRepository = new CountingOpportunityContextRepository(initialContext, refreshedContext);
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var whatsappAgent = new StubWhatsappConversationAnalysisAgent(new WhatsappConversationAnalysisResult(
            "Cliente pediu retorno com proposta.",
            true,
            "Cliente quer receber proposta atualizada.",
            true,
            "Enviar proposta",
            "Retornar com valores atualizados.",
            null,
            88,
            ["Houve proximo passo comercial claro."]));
        var actionStore = new CountingWhatsappConversationActionStore();
        var processor = new OpportunityAnalysisEventProcessor(
            contextRepository,
            riskAgent,
            resultStore,
            whatsappAgent,
            actionStore,
            new CountingWhatsappConversationAnalysisScheduler(),
            new NullMeetingAudioAnalysisService());

        await processor.ProcessAsync(initialContext.TriggerEvent, CancellationToken.None);

        Assert.Equal(2, contextRepository.Calls);
        Assert.Equal(1, whatsappAgent.Calls);
        Assert.Equal(1, actionStore.Calls);
        Assert.Same(initialContext, actionStore.LastContext);
        Assert.Equal(1, riskAgent.Calls);
        Assert.Same(refreshedContext, riskAgent.LastContext);
        Assert.Equal(1, resultStore.Calls);
    }

    [Fact]
    public async Task ProcessAsync_RunsRiskAnalysisWithoutApplyingWhatsappStore_WhenWhatsappAgentReturnsNull()
    {
        var context = CreateContext(CreateEvent("opportunity.whatsapp.conversation.batch"));
        var contextRepository = new CountingOpportunityContextRepository(context);
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var whatsappAgent = new StubWhatsappConversationAnalysisAgent(null);
        var actionStore = new CountingWhatsappConversationActionStore();
        var processor = new OpportunityAnalysisEventProcessor(
            contextRepository,
            riskAgent,
            resultStore,
            whatsappAgent,
            actionStore,
            new CountingWhatsappConversationAnalysisScheduler(),
            new NullMeetingAudioAnalysisService());

        await processor.ProcessAsync(context.TriggerEvent, CancellationToken.None);

        Assert.Equal(1, contextRepository.Calls);
        Assert.Equal(1, whatsappAgent.Calls);
        Assert.Equal(0, actionStore.Calls);
        Assert.Equal(1, riskAgent.Calls);
        Assert.Same(context, riskAgent.LastContext);
        Assert.Equal(1, resultStore.Calls);
    }

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
            new CountingWhatsappConversationActionStore(),
            new CountingWhatsappConversationAnalysisScheduler(),
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

    [Theory]
    [InlineData("opportunity.activity.created")]
    [InlineData("opportunity.activity.updated")]
    [InlineData("activity.created")]
    [InlineData("activity.updated")]
    public async Task ActivityProcessor_RunsOpportunityRiskAnalysis_ForActivityEvents(string eventType)
    {
        var activityEvent = CreateEvent(eventType);
        var context = CreateContext(activityEvent);
        var contextRepository = new CountingOpportunityContextRepository(context);
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var processor = new ActivityAnalysisEventProcessor(contextRepository, riskAgent, resultStore);

        await processor.ProcessAsync(activityEvent, CancellationToken.None);

        Assert.Equal(1, contextRepository.Calls);
        Assert.Equal(1, riskAgent.Calls);
        Assert.Equal(1, resultStore.Calls);
        Assert.Same(context, riskAgent.LastContext);
    }

    [Theory]
    [InlineData("won")]
    [InlineData("lost")]
    public async Task Processors_DoNotAnalyzeRisk_ForClosedOpportunities(string status)
    {
        var opportunityEvent = CreateEvent("opportunity.updated");
        var opportunityContext = CreateContext(opportunityEvent, status);
        var opportunityRiskAgent = new CountingRiskAnalysisAgent();
        var opportunityStore = new CountingAnalysisResultStore();
        var opportunityProcessor = new OpportunityAnalysisEventProcessor(
            new CountingOpportunityContextRepository(opportunityContext),
            opportunityRiskAgent,
            opportunityStore,
            new NullWhatsappConversationAnalysisAgent(),
            new CountingWhatsappConversationActionStore(),
            new CountingWhatsappConversationAnalysisScheduler(),
            new NullMeetingAudioAnalysisService());

        await opportunityProcessor.ProcessAsync(opportunityEvent, CancellationToken.None);

        var activityEvent = CreateEvent("activity.updated");
        var activityRiskAgent = new CountingRiskAnalysisAgent();
        var activityStore = new CountingAnalysisResultStore();
        var activityProcessor = new ActivityAnalysisEventProcessor(
            new CountingOpportunityContextRepository(CreateContext(activityEvent, status)),
            activityRiskAgent,
            activityStore);

        await activityProcessor.ProcessAsync(activityEvent, CancellationToken.None);

        Assert.Equal(0, opportunityRiskAgent.Calls);
        Assert.Equal(0, opportunityStore.Calls);
        Assert.Equal(0, activityRiskAgent.Calls);
        Assert.Equal(0, activityStore.Calls);
    }

    [Fact]
    public async Task ProcessAsync_RunsOpportunityRiskAnalysis_AfterMeetingAudioTranscription()
    {
        var meetingEvent = CreateEvent("opportunity.meeting_audio.recording.created");
        var context = CreateContext(meetingEvent);
        var contextRepository = new CountingOpportunityContextRepository(context);
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var meetingService = new CountingMeetingAudioAnalysisService();
        var processor = new OpportunityAnalysisEventProcessor(
            contextRepository,
            riskAgent,
            resultStore,
            new NullWhatsappConversationAnalysisAgent(),
            new CountingWhatsappConversationActionStore(),
            new CountingWhatsappConversationAnalysisScheduler(),
            meetingService);

        await processor.ProcessAsync(meetingEvent, CancellationToken.None);

        Assert.Equal(1, meetingService.Calls);
        Assert.Equal(1, contextRepository.Calls);
        Assert.Equal(1, riskAgent.Calls);
        Assert.Equal(1, resultStore.Calls);
    }

    [Fact]
    public async Task ProcessAsync_TranscribesWhatsappCall_WithoutOpportunityContext()
    {
        var callEvent = new OpportunityEvent(
            Guid.NewGuid().ToString(),
            "opportunity.meeting_audio.recording.created",
            DateTime.UtcNow,
            string.Empty,
            Guid.NewGuid().ToString(),
            new Dictionary<string, object?> { ["recordingId"] = Guid.NewGuid().ToString(), ["sourceKind"] = "whatsapp_call" });
        var contextRepository = new CountingOpportunityContextRepository();
        var meetingService = new CountingMeetingAudioAnalysisService();
        var riskAgent = new CountingRiskAnalysisAgent();
        var resultStore = new CountingAnalysisResultStore();
        var processor = new OpportunityAnalysisEventProcessor(
            contextRepository,
            riskAgent,
            resultStore,
            new NullWhatsappConversationAnalysisAgent(),
            new CountingWhatsappConversationActionStore(),
            new CountingWhatsappConversationAnalysisScheduler(),
            meetingService);

        await processor.ProcessAsync(callEvent, CancellationToken.None);

        Assert.Equal(1, meetingService.Calls);
        Assert.Equal(1, contextRepository.Calls);
        Assert.Equal(0, riskAgent.Calls);
        Assert.Equal(0, resultStore.Calls);
    }

    private sealed class CountingOpportunityContextRepository : IOpportunityContextRepository
    {
        private readonly Queue<OpportunityAnalysisContext?> _contexts;

        public CountingOpportunityContextRepository(params OpportunityAnalysisContext?[] contexts)
        {
            _contexts = new Queue<OpportunityAnalysisContext?>(contexts);
        }

        public int Calls { get; private set; }

        public Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent triggerEvent, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_contexts.Count == 0 ? null : _contexts.Dequeue());
        }
    }

    private sealed class CountingRiskAnalysisAgent : IRiskAnalysisAgent
    {
        public int Calls { get; private set; }
        public OpportunityAnalysisContext? LastContext { get; private set; }

        public Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastContext = context;
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
        public OpportunityAnalysisContext? LastContext { get; private set; }

        public Task SaveRiskAnalysisAsync(OpportunityAnalysisContext context, RiskAnalysisResult result, CancellationToken cancellationToken)
        {
            Calls++;
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class NullWhatsappConversationAnalysisAgent : IWhatsappConversationAnalysisAgent
    {
        public Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken) =>
            Task.FromResult<WhatsappConversationAnalysisResult?>(null);
    }

    private sealed class StubWhatsappConversationAnalysisAgent(WhatsappConversationAnalysisResult? result) : IWhatsappConversationAnalysisAgent
    {
        public int Calls { get; private set; }

        public Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class CountingWhatsappConversationActionStore : IWhatsappConversationActionStore
    {
        public int Calls { get; private set; }
        public OpportunityAnalysisContext? LastContext { get; private set; }

        public Task ApplyAsync(OpportunityAnalysisContext context, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken)
        {
            Calls++;
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingWhatsappConversationAnalysisScheduler : IWhatsappConversationAnalysisScheduler
    {
        public int ScheduleCalls { get; private set; }
        public OpportunityEvent? LastScheduledEvent { get; private set; }

        public Task ScheduleAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
        {
            ScheduleCalls++;
            LastScheduledEvent = opportunityEvent;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<OpportunityEvent>> ClaimDueAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<OpportunityEvent>>([]);

        public Task CompleteAsync(string eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FailAsync(string eventId, string error, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullMeetingAudioAnalysisService : IMeetingAudioAnalysisService
    {
        public Task<bool> ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class CountingMeetingAudioAnalysisService : IMeetingAudioAnalysisService
    {
        public int Calls { get; private set; }

        public Task<bool> ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private static OpportunityEvent CreateEvent(string type) =>
        new(
            Guid.NewGuid().ToString(),
            type,
            DateTime.UtcNow,
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            new Dictionary<string, object?>
            {
                ["conversationId"] = Guid.NewGuid().ToString(),
                ["contactId"] = Guid.NewGuid().ToString(),
                ["text"] = "Cliente pediu proposta atualizada."
            });

    private static OpportunityAnalysisContext CreateContext(OpportunityEvent triggerEvent, string status = "active")
    {
        var pipelineId = Guid.NewGuid().ToString();
        var stageId = Guid.NewGuid().ToString();
        return new OpportunityAnalysisContext(
            new OpportunitySnapshot(triggerEvent.OpportunityId, Guid.NewGuid().ToString(), "Oportunidade WhatsApp", pipelineId, stageId, null, triggerEvent.UserId, 1000m, status, false, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow, null),
            new PipelineStageSnapshot(stageId, "Negociacao", 1),
            [],
            [],
            [],
            [],
            [],
            null,
            [],
            [],
            [],
            triggerEvent);
    }
}
