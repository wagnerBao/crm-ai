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
    public async Task AnalyzeAsync_ConvertsConcreteNextStepIntoActivitySuggestion_WhenModelLeavesActivityUnset()
    {
        var nextStep = "Responsável sugerido: vendedor do Skopos. Enviar a Raphael um e-mail de acompanhamento com as atualizações do produto.";
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "Raphael solicitou acompanhamento por e-mail.",
            false,
            null,
            false,
            null,
            null,
            null,
            88,
            ["Existe um próximo passo comercial claro."],
            NextSteps: [nextStep]));

        var result = await CreateAgent(openAiClient)
            .AnalyzeAsync(CreateContext("Pode me atualizar por e-mail?", null), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.ShouldCreateActivity);
        Assert.Equal("Enviar a Raphael um e-mail de acompanhamento com as atualizações do produto", result.ActivityTitle);
        Assert.Equal($"- {nextStep}", result.ActivityNotes);
        Assert.Equal("next_step_enviar_a_raphael_um_e_mail_de_acompanhamento_com_as_atualizacoes_do_produto", result.ActivityIntentKey);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotCreateActivity_WhenThereIsNoConcreteNextStepOrCompleteSuggestion()
    {
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "Conversa sem pendências.",
            false,
            null,
            true,
            null,
            null,
            null,
            80,
            ["Nenhuma ação identificada."],
            NextSteps: []));

        var result = await CreateAgent(openAiClient)
            .AnalyzeAsync(CreateContext("Obrigado, está tudo certo.", null), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.ShouldCreateActivity);
        Assert.Null(result.ActivityTitle);
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

    [Fact]
    public async Task AnalyzeAsync_Generates_Incremental_Scorecard_In_The_Same_OpenAi_Call()
    {
        var criterionId = Guid.NewGuid().ToString();
        var template = new WhatsappScorecardContext(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            2,
            "Atendimento WhatsApp",
            [new WhatsappScorecardCriterionContext(
                criterionId,
                "response_cadence",
                "Cadência de resposta",
                null,
                100m,
                "Avalie tempo e continuidade.",
                [],
                [],
                0,
                100,
                true)],
            [new WhatsappPreviousScorecardItemInput(
                "response_cadence",
                70,
                80,
                "A equipe respondeu no mesmo turno.",
                null,
                [new OpenAiConversationEvidence("Equipe: retorno em seguida", "Equipe", null, null, "transcript", 80)])]);
        var openAiClient = new FakeOpenAiWhatsappConversationAnalysisClient(new OpenAiWhatsappConversationAnalysisResponse(
            "Cliente recebeu o retorno.",
            false,
            null,
            false,
            null,
            null,
            null,
            88,
            ["Atendimento atualizado."],
            ScorecardItems: [new OpenAiConversationScorecardItem(
                "response_cadence",
                90,
                92,
                "Resposta objetiva no novo trecho.",
                "Manter o padrão.",
                [new OpenAiConversationEvidence("[2026-08-18 10:05] Equipe: Já confirmei para você.", "Equipe", 4_294_967_296L, 4_294_967_396L, "transcript", 92)])]));
        var scorecardRepository = new FakeScorecardContextRepository(template);
        var agent = CreateAgent(openAiClient, scorecardRepository: scorecardRepository);

        var result = await agent.AnalyzeAsync(
            CreateContext("[2026-08-18 10:05] Equipe: Já confirmei para você.", "Resumo anterior."),
            CancellationToken.None);

        Assert.NotNull(result?.Scorecard);
        Assert.Equal(1, openAiClient.Calls);
        Assert.Equal(2, openAiClient.LastInput!.ScorecardTemplate!.Version);
        Assert.Single(openAiClient.LastInput.ScorecardTemplate.PreviousDailyItems);
        var item = Assert.Single(result.Scorecard.Items);
        Assert.Equal(criterionId, item.CriterionId);
        Assert.Equal(90, item.Score);
        Assert.Equal(92, item.ConfidenceScore);
        Assert.Single(item.Evidence);
    }

    private static WhatsappConversationAnalysisAgent CreateAgent(
        IOpenAiWhatsappConversationAnalysisClient openAiClient,
        WhatsappSuggestionSemanticContext? semanticContext = null,
        IWhatsappScorecardContextRepository? scorecardRepository = null) =>
        new(openAiClient, new FakeAgentSettingsRepository(), new FakeSuggestionContextRepository(semanticContext), scorecardRepository);

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

    private sealed class FakeScorecardContextRepository(WhatsappScorecardContext context) : IWhatsappScorecardContextRepository
    {
        public Task<WhatsappScorecardContext?> GetAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken) =>
            Task.FromResult<WhatsappScorecardContext?>(context);
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
