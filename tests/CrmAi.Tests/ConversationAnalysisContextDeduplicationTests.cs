using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CrmAi.Application;
using Microsoft.Extensions.Options;

namespace CrmAi.Tests;

public sealed class ConversationAnalysisContextDeduplicationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private const string ConfiguredContext = "Priorize o pedido de proposta do cliente.\nNao confunda o contato externo com o responsavel interno.";

    [Theory]
    [InlineData("whatsapp", "context")]
    [InlineData("instagram", "context")]
    [InlineData("whatsapp", "prompt")]
    [InlineData("instagram", "prompt")]
    [InlineData("whatsapp", "instructions")]
    [InlineData("instagram", "instructions")]
    public async Task Exact_configured_context_is_sent_once_without_changing_other_request_fields(string channel, string duplicateSource)
    {
        var settings = Settings(channel);
        var duplicate = duplicateSource switch
        {
            "prompt" => settings.SystemPrompt,
            "instructions" => settings.Instructions,
            _ => settings.ContextInstructions!
        };
        var input = Input(duplicate);
        var originalJson = JsonSerializer.Serialize(input, JsonOptions);
        var handler = new ResponsesHandler();
        var logs = new CapturingLogStore();
        var client = Client(handler, logs);
        var invocation = Invocation(channel);

        var result = await client.AnalyzeAsync(settings, input, invocation, CancellationToken.None);
        await client.AnalyzeAsync(settings, input with { AdditionalContext = null }, invocation, CancellationToken.None);

        // The optimized request must equal one constructed without the duplicate, including
        // stable instructions, model/reasoning, strict schema and every remaining input field.
        Assert.Equal(handler.Requests[1], handler.Requests[0]);
        using var request = JsonDocument.Parse(handler.Requests[0]);
        var sentInput = JsonNode.Parse(request.RootElement.GetProperty("input").GetString()!)!.AsObject();
        Assert.False(sentInput.ContainsKey("additionalContext"));
        var expectedInput = JsonSerializer.SerializeToNode(input with { AdditionalContext = null }, JsonOptions);
        Assert.True(JsonNode.DeepEquals(expectedInput, sentInput));
        Assert.Equal(settings.Model, request.RootElement.GetProperty("model").GetString());
        Assert.StartsWith(settings.Instructions, request.RootElement.GetProperty("instructions").GetString());
        Assert.Contains(duplicate, request.RootElement.GetProperty("instructions").GetString()!);
        Assert.True(request.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(originalJson, JsonSerializer.Serialize(input, JsonOptions));
        Assert.True(request.RootElement.GetProperty("input").GetString()!.Length < originalJson.Length);
        Assert.Equal("Pedido de proposta recebido.", result.ConversationSummary);
        Assert.Equal(invocation, logs.CreditChecks[0]);
        Assert.Equal(invocation, logs.Entries[0].Context);
        Assert.Equal(handler.Requests[0], logs.Entries[0].RequestJson);
        Assert.Equal(1200, logs.Entries[0].Usage.PromptTokens);
        Assert.Equal(900, logs.Entries[0].Usage.CachedPromptTokens);
        Assert.Equal($"responses.{channel}-conversation-analysis", logs.Entries[0].Operation);
    }

    public static IEnumerable<object?[]> PreservedContexts()
    {
        foreach (var channel in new[] { "whatsapp", "instagram" })
        {
            yield return [channel, ConfiguredContext, null];
            yield return [channel, "A proposta agora inclui um produto diferente.", ConfiguredContext];
            yield return [channel, "pedido de proposta", ConfiguredContext];
            yield return [channel, ConfiguredContext.ToUpperInvariant(), ConfiguredContext];
            yield return [channel, $" {ConfiguredContext} ", ConfiguredContext];
            yield return [channel, ConfiguredContext.Replace("\n", "\r\n"), ConfiguredContext];
            yield return [channel, null, ConfiguredContext];
            yield return [channel, "", ConfiguredContext];
            yield return [channel, "  ", "  "];
        }
    }

    [Theory]
    [MemberData(nameof(PreservedContexts))]
    public async Task Distinct_partial_or_absent_context_preserves_the_original_input(
        string channel, string? additionalContext, string? configuredContext)
    {
        var input = Input(additionalContext);
        var handler = new ResponsesHandler();

        await Client(handler, new CapturingLogStore()).AnalyzeAsync(
            Settings(channel) with { ContextInstructions = configuredContext }, input, Invocation(channel), CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal(JsonSerializer.Serialize(input, JsonOptions), request.RootElement.GetProperty("input").GetString());
    }

    [Theory]
    [InlineData("whatsapp")]
    [InlineData("instagram")]
    public async Task Failure_log_records_the_same_deduplicated_payload_sent_to_the_provider(string channel)
    {
        var handler = new ResponsesHandler(HttpStatusCode.BadRequest);
        var logs = new CapturingLogStore();
        var invocation = Invocation(channel);

        await Assert.ThrowsAsync<OpenAiRequestException>(() => Client(handler, logs).AnalyzeAsync(
            Settings(channel), Input(ConfiguredContext), invocation, CancellationToken.None));

        var entry = Assert.Single(logs.Entries);
        Assert.False(entry.Success);
        Assert.Equal(Assert.Single(handler.Requests), entry.RequestJson);
        Assert.Equal(invocation, entry.Context);
        using var request = JsonDocument.Parse(entry.RequestJson);
        using var input = JsonDocument.Parse(request.RootElement.GetProperty("input").GetString()!);
        Assert.False(input.RootElement.TryGetProperty("additionalContext", out _));
        Assert.Contains(ConfiguredContext, request.RootElement.GetProperty("instructions").GetString()!);
    }

    private static AiAgentRuntimeSettings Settings(string channel) => new(
        $"{channel}-conversation-analysis", true, "openai", "gpt-5.6-luna", "synthetic-test-key",
        "Analise apenas os fatos recebidos e gere JSON no schema solicitado.", 10, ConfiguredContext,
        ["opportunity", "account", "activities", "notes", "contacts", "users", "history", "agent_insights"], "America/Sao_Paulo");

    private static AiAgentInvocationContext Invocation(string channel) => new(
        PlatformArea: channel, CompanyId: "synthetic-company", OpportunityId: "synthetic-opportunity",
        WhatsappConversationId: channel == "whatsapp" ? "synthetic-conversation" : null,
        AccountId: "synthetic-account", ContactId: "synthetic-contact", UserId: "synthetic-owner",
        ContextEntityKeys: ["activities", "notes"], Metadata: new Dictionary<string, object?> { ["eventId"] = "synthetic-event" });

    private static WhatsappConversationAnalysisInput Input(string? context)
    {
        var at = new DateTime(2026, 10, 13, 14, 0, 0, DateTimeKind.Utc);
        return new WhatsappConversationAnalysisInput(
            new("synthetic-opportunity", "Proposta de servico", "active", "Proposta", 150m),
            new("synthetic-conversation", "synthetic-contact", 2, at.AddMinutes(-5), at, at.AddMinutes(-10)),
            new("synthetic-account", "Empresa exemplo", "Servicos", "Sao Paulo", "SP", "active"),
            [new("synthetic-product", "Servico exemplo", "service", 150m, false, "active", "Instalacao solicitada.")],
            new(1, 0, [new("Preparar proposta", "follow-up", "WhatsApp", "pending", at.AddDays(1), "Incluir instalacao.", null, "synthetic-owner")]),
            [new("Cliente solicitou prazo de entrega.", "synthetic-owner", at.AddMinutes(-8))],
            [new("Cliente exemplo", "Comprador", "active", "synthetic-owner", "synthetic-contact")],
            [new("Responsavel exemplo", "Vendedor", true, "synthetic-owner")],
            [new("Mudou para etapa Proposta.", "synthetic-owner", at.AddMinutes(-9))],
            [new("Prazo solicitado", "Confirmar prazo antes de enviar a proposta.", "conversation-analysis", .9m, "applied", at.AddMinutes(-7))],
            "Cliente demonstrou interesse no servico.",
            "[13/10/2026 10:55] Equipe: Qual servico?\n[13/10/2026 11:00] Cliente: Envie a proposta com instalacao.",
            context, at)
        {
            Participants = new("synthetic-contact", "Cliente exemplo", "synthetic-owner", "Responsavel exemplo", "Equipe comercial"),
            ExistingSuggestions = [new("synthetic-suggestion", "activity", "pending", "Enviar proposta", "Incluir instalacao", at.AddDays(1), "enviar_proposta", at.AddMinutes(-2))],
            ExistingOpenOpportunities = [new("synthetic-open-opportunity", "Proposta de servico", "Comercial", "Proposta", at.AddMinutes(-3))],
            ScorecardTemplate = new("synthetic-template", "Atendimento", 1,
                [new("follow-up", "Proximo passo", "Confirmar acao", 1m, "Use evidencias literais.", ["Enviar proposta"], ["Sem proximo passo"], 0, 10, true)],
                [new("follow-up", 5, 80, "Proposta em preparacao.", "Confirmar instalacao.", [])]),
            TimeZoneId = "America/Sao_Paulo"
        };
    }

    private static OpenAiResponsesWhatsappConversationAnalysisClient Client(HttpMessageHandler handler, IAiAgentInvocationLogStore logs) =>
        new(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), logs);

    private sealed class ResponsesHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var result = new OpenAiWhatsappConversationAnalysisResponse("Pedido de proposta recebido.", false, null, false, null, null, null, 90, []);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK
                    ? JsonSerializer.Serialize(new
                    {
                        output = new[] { new { content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(result, JsonOptions) } } } },
                        usage = new { input_tokens = 1200, output_tokens = 100, total_tokens = 1300, input_tokens_details = new { cached_tokens = 900 } }
                    })
                    : "{\"error\":{\"code\":\"synthetic_bad_request\"}}")
            };
        }
    }

    private sealed class CapturingLogStore : IAiAgentInvocationLogStore
    {
        public List<AiAgentInvocationContext> CreditChecks { get; } = [];
        public List<AiAgentInvocationLogEntry> Entries { get; } = [];

        public Task EnsureCreditsAvailableAsync(AiAgentInvocationContext context, CancellationToken cancellationToken)
        {
            CreditChecks.Add(context);
            return Task.CompletedTask;
        }

        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
