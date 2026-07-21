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

    [Fact]
    public async Task AnalyzeAsync_Uses_OpenAi_To_Match_Existing_Suggestion_By_Semantic_Intent()
    {
        var suggestionId = Guid.NewGuid().ToString();
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "Cliente aguarda retorno da proposta.",
            false,
            null,
            true,
            "Ligar para revisar a proposta",
            "Confirmar os valores e condições.",
            "2026-07-05T15:00:00Z",
            90,
            ["Mesmo compromisso comercial já sugerido."],
            ActivityMatchingSuggestionId: suggestionId,
            ActivityIntentKey: "follow_up_proposta"));
        var semanticContext = new WhatsappSuggestionSemanticContext(
            [new WhatsappSuggestionCandidate(suggestionId, "activity", "pending", "Retornar sobre valores", "Validar proposta com o cliente.", null, null, DateTime.UtcNow)],
            []);

        var result = await CreateAgent(openAiClient, semanticContext)
            .AnalyzeAsync(CreateContext("Pode me ligar para vermos a proposta?", "Proposta enviada."), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(suggestionId, result.ActivityMatchingSuggestionId);
        Assert.Equal("follow_up_proposta", result.ActivityIntentKey);
        Assert.Single(openAiClient.LastInput!.ExistingSuggestions);
        Assert.Equal(suggestionId, openAiClient.LastInput.ExistingSuggestions.Single().Id);
    }

    [Fact]
    public async Task AnalyzeAsync_Ignores_Matching_Ids_That_Were_Not_Provided_To_OpenAi()
    {
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "Cliente quer uma nova proposta.",
            false,
            null,
            false,
            null,
            null,
            null,
            85,
            ["Nova necessidade comercial."],
            OpportunityMatchingSuggestionId: Guid.NewGuid().ToString(),
            OpportunityIntentKey: "nova_proposta",
            MatchingOpenOpportunityId: Guid.NewGuid().ToString()));

        var result = await CreateAgent(openAiClient)
            .AnalyzeAsync(CreateContext("Quero uma proposta para outro veiculo.", null), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result.OpportunityMatchingSuggestionId);
        Assert.Null(result.MatchingOpenOpportunityId);
    }

    private static WhatsappConversationAnalysisAgent CreateAgent(
        IOpenAiWhatsappConversationAnalysisClient openAiClient,
        WhatsappSuggestionSemanticContext? semanticContext = null) =>
        new(openAiClient, new FakeAgentSettingsRepository(), new FakeSuggestionContextRepository(semanticContext));

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
        public WhatsappConversationAnalysisInput? LastInput { get; private set; }

        public Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(
            AiAgentRuntimeSettings settings,
            WhatsappConversationAnalysisInput input,
            AiAgentInvocationContext invocationContext,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastInput = input;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeSuggestionContextRepository(WhatsappSuggestionSemanticContext? context) : IWhatsappSuggestionContextRepository
    {
        public Task<WhatsappSuggestionSemanticContext> GetAsync(string? companyId, string? contactId, CancellationToken cancellationToken) =>
            Task.FromResult(context ?? WhatsappSuggestionSemanticContext.Empty);
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
