using CrmAi.Application;
using CrmAi.Domain;

namespace CrmAi.Tests;

public sealed class RiskAnalysisAgentTests
{
    [Fact]
    public async Task AnalyzeAsync_UsesOpenAiAgentResponse_ForRiskResult()
    {
        var now = new DateTime(2026, 05, 06, 12, 0, 0, DateTimeKind.Utc);
        var openAiClient = new FakeOpenAiRiskAnalysisClient(new OpenAiRiskAnalysisResponse(
            "HIGH",
            82,
            ["Existem atividades atrasadas e regra critica aplicada."],
            ["Reagendar as atividades atrasadas com responsavel definido."]));

        var context = CreateContext(
            now,
            activities:
            [
                PendingActivity(now.AddDays(-5)),
                PendingActivity(now.AddDays(-4))
            ],
            notes: [],
            history: [],
            metricRules:
            [
                new CommercialAnalysisMetricRuleSnapshot(
                    Guid.NewGuid().ToString(),
                    "overdue_activities",
                    null,
                    null,
                    "critical",
                    ">",
                    0,
                    "count")
            ]);

        var result = await CreateAgent(openAiClient).AnalyzeAsync(context, CancellationToken.None);

        Assert.Equal(RiskLevel.High, result.RiskLevel);
        Assert.Equal(82, result.RiskScore);
        Assert.Contains(result.Reasons, reason => reason.Contains("regra critica", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Recommendations, recommendation => recommendation.Contains("Reagendar", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeAsync_SendsSynthesizedCommercialRules_ToOpenAiAgent()
    {
        var now = new DateTime(2026, 05, 06, 12, 0, 0, DateTimeKind.Utc);
        var openAiClient = new FakeOpenAiRiskAnalysisClient(new OpenAiRiskAnalysisResponse(
            "MEDIUM",
            55,
            ["A parametrizacao comercial marcou atividade atrasada."],
            ["Resolver a pendencia antes de avancar fase."]));

        var context = CreateContext(
            now,
            activities:
            [
                PendingActivity(now.AddDays(-1))
            ],
            notes: [],
            history: [],
            metricRules:
            [
                new CommercialAnalysisMetricRuleSnapshot(
                    Guid.NewGuid().ToString(),
                    "overdue_activities",
                    null,
                    null,
                    "critical",
                    ">",
                    0,
                    "count")
            ]);

        var result = await CreateAgent(openAiClient).AnalyzeAsync(context, CancellationToken.None);
        var overdueMetric = Assert.Single(openAiClient.LastInput!.CommercialRuleAssessment.Metrics, metric => metric.MetricKey == "overdue_activities");

        Assert.Equal(65, result.SnapshotUpdate.HealthScore);
        Assert.Equal(37, result.SnapshotUpdate.ConfidenceScore);
        Assert.Equal(1, result.SnapshotUpdate.ActivitiesOpen);
        Assert.Equal(1, result.SnapshotUpdate.ActivitiesOverdue);
        Assert.Equal(now, result.SnapshotUpdate.LastInteractionAt);
        Assert.Equal(1, overdueMetric.Value);
        Assert.NotNull(overdueMetric.AppliedRule);
        Assert.Equal("critical", overdueMetric.AppliedRule!.Level);
        Assert.True(overdueMetric.AppliedRule.Matched);
    }

    [Fact]
    public async Task AnalyzeAsync_PreservesLowRisk_WhenOpenAiAgentClassifiesLow()
    {
        var now = new DateTime(2026, 05, 06, 12, 0, 0, DateTimeKind.Utc);
        var openAiClient = new FakeOpenAiRiskAnalysisClient(new OpenAiRiskAnalysisResponse(
            "LOW",
            18,
            ["Pipeline ativo e sem regras comerciais violadas."],
            ["Manter a cadencia registrada."]));
        var context = CreateContext(
            now,
            activities:
            [
                new ActivitySnapshot(Guid.NewGuid().ToString(), "Ligacao", "call", "phone", "done", now.AddDays(-1), null, null, null, now.AddDays(-1), now.AddDays(-1))
            ],
            notes:
            [
                new NoteSnapshot(Guid.NewGuid().ToString(), "Cliente confirmou interesse.", null, now.AddDays(-1))
            ],
            history:
            [
                new HistoryEventSnapshot(Guid.NewGuid().ToString(), "opportunity.stage.changed", null, now.AddDays(-2))
            ]);

        var result = await CreateAgent(openAiClient).AnalyzeAsync(context, CancellationToken.None);

        Assert.Equal(RiskLevel.Low, result.RiskLevel);
        Assert.Equal(18, result.RiskScore);
    }

    private static OpportunityAnalysisContext CreateContext(
        DateTime now,
        IReadOnlyCollection<ActivitySnapshot> activities,
        IReadOnlyCollection<NoteSnapshot> notes,
        IReadOnlyCollection<HistoryEventSnapshot> history,
        IReadOnlyCollection<CommercialAnalysisMetricRuleSnapshot>? metricRules = null)
    {
        var pipelineId = Guid.NewGuid().ToString();
        var stageId = Guid.NewGuid().ToString();
        return new(
            new OpportunitySnapshot(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "Teste", pipelineId, stageId, null, null, 1000m, "active", false, now.AddDays(-20), now.AddDays(-20), null),
            new PipelineStageSnapshot(stageId, "Negociacao", 2),
            notes,
            activities,
            [new ContactSnapshot(Guid.NewGuid().ToString(), null, "Cliente", "Diretor", "cliente@example.com", null, null, "active")],
            [],
            history,
            null,
            [],
            [],
            metricRules ?? [],
            new OpportunityEvent(Guid.NewGuid().ToString(), "opportunity.activity.created", now, Guid.NewGuid().ToString(), null, new Dictionary<string, object?>()));
    }

    private static ActivitySnapshot PendingActivity(DateTime date)
        => new(Guid.NewGuid().ToString(), "Pendente", "task", "email", "pending", date, null, null, null, date, date);

    private static RiskAnalysisAgent CreateAgent(IOpenAiRiskAnalysisClient openAiClient)
        => new(openAiClient, new FakeAgentSettingsRepository(), new RiskAnalysisAgentInputBuilder(new CommercialRuleAssessmentService()));

    private sealed class FakeOpenAiRiskAnalysisClient(OpenAiRiskAnalysisResponse response) : IOpenAiRiskAnalysisClient
    {
        public RiskAnalysisAgentInput? LastInput { get; private set; }

        public Task<OpenAiRiskAnalysisResponse> AnalyzeAsync(
            AiAgentRuntimeSettings settings,
            RiskAnalysisAgentInput input,
            CancellationToken cancellationToken)
        {
            LastInput = input;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeAgentSettingsRepository : IAiAgentRuntimeSettingsRepository
    {
        public Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAgentRuntimeSettings(
                agentKey,
                true,
                "openai",
                "gpt-4.1-mini",
                null,
                "Analise risco e responda no schema solicitado.",
                1,
                null,
                ["opportunity", "account", "products", "activities", "notes", "contacts", "users", "history", "agent_insights"]));
    }
}
