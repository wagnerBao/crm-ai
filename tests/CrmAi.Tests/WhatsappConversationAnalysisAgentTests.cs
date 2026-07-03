using CrmAi.Application;
using CrmAi.Domain;

namespace CrmAi.Tests;

public sealed class WhatsappConversationAnalysisAgentTests
{
    [Fact]
    public async Task AnalyzeAsync_ReturnsNullAndDoesNotCallOpenAi_WhenNewTranscriptIsEmpty()
    {
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "Resumo",
            false,
            null,
            false,
            null,
            null,
            null,
            80,
            ["Conversa analisada."]));
        var context = CreateContext(text: "   ", previousSummary: "Resumo anterior");

        var result = await CreateAgent(openAiClient).AnalyzeAsync(context, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, openAiClient.Calls);
    }

    [Fact]
    public async Task AnalyzeAsync_NormalizesOpenAiResponse()
    {
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "  Cliente pediu proposta atualizada.  ",
            true,
            "  Registrar preferencia por pagamento parcelado.  ",
            true,
            "  Enviar proposta  ",
            "  Retornar com valores atualizados.  ",
            "2026-07-04T15:00:00Z",
            140,
            ["  Proximo passo claro.  ", "Proximo passo claro.", "  "]));
        var context = CreateContext(text: "Cliente pediu proposta atualizada.", previousSummary: "Resumo anterior");

        var result = await CreateAgent(openAiClient).AnalyzeAsync(context, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Cliente pediu proposta atualizada.", result.ConversationSummary);
        Assert.True(result.ShouldCreateNote);
        Assert.Equal("Registrar preferencia por pagamento parcelado.", result.NoteText);
        Assert.True(result.ShouldCreateActivity);
        Assert.Equal("Enviar proposta", result.ActivityTitle);
        Assert.Equal("Retornar com valores atualizados.", result.ActivityNotes);
        Assert.Equal(new DateTime(2026, 07, 04, 15, 0, 0, DateTimeKind.Utc), result.ActivityDueAt);
        Assert.Equal(100, result.ConfidenceScore);
        Assert.Equal(["Proximo passo claro."], result.Reasons);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesPreviousSummaryFallback_WhenOpenAiSummaryIsEmpty()
    {
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            " ",
            false,
            " ",
            false,
            " ",
            " ",
            "sem data",
            -10,
            []));
        var context = CreateContext(text: "Novo trecho da conversa.", previousSummary: "Resumo acumulado anterior.");

        var result = await CreateAgent(openAiClient).AnalyzeAsync(context, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Resumo acumulado anterior.", result.ConversationSummary);
        Assert.Null(result.NoteText);
        Assert.Null(result.ActivityTitle);
        Assert.Null(result.ActivityNotes);
        Assert.Null(result.ActivityDueAt);
        Assert.Equal(0, result.ConfidenceScore);
        Assert.Equal(["Conversa analisada e consolidada."], result.Reasons);
    }

    private static WhatsappConversationAnalysisAgent CreateAgent(IOpenAiWhatsappConversationAnalysisClient openAiClient) =>
        new(openAiClient, new FakeAgentSettingsRepository());

    private static OpportunityAnalysisContext CreateContext(string text, string? previousSummary)
    {
        var opportunityId = Guid.NewGuid().ToString();
        var pipelineId = Guid.NewGuid().ToString();
        var stageId = Guid.NewGuid().ToString();
        return new OpportunityAnalysisContext(
            new OpportunitySnapshot(opportunityId, Guid.NewGuid().ToString(), "Oportunidade WhatsApp", pipelineId, stageId, null, Guid.NewGuid().ToString(), 1000m, "active", false, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow, null),
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
            new OpportunityEvent(
                Guid.NewGuid().ToString(),
                "opportunity.whatsapp.conversation.batch",
                DateTime.UtcNow,
                opportunityId,
                null,
                new Dictionary<string, object?>
                {
                    ["conversationId"] = Guid.NewGuid().ToString(),
                    ["contactId"] = Guid.NewGuid().ToString(),
                    ["previousSummary"] = previousSummary,
                    ["text"] = text
                }));
    }

    private sealed class FakeOpenAiWhatsappConversationAnalysisClient(OpenAiWhatsappConversationAnalysisResponse response)
        : IOpenAiWhatsappConversationAnalysisClient
    {
        public int Calls { get; private set; }

        public Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(
            AiAgentRuntimeSettings settings,
            WhatsappConversationAnalysisInput input,
            AiAgentInvocationContext invocationContext,
            CancellationToken cancellationToken)
        {
            Calls++;
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
                "Analise a conversa e responda no schema solicitado.",
                10,
                null,
                ["opportunity", "account", "activities", "notes", "agent_insights"]));
    }
}
