using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrmAi.Application;
using CrmAi.Domain;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace CrmAi.Tests;

public sealed class AiContextOptimizationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Checkout_compaction_preserves_every_row_and_priority_group_without_mutating_the_dashboard()
    {
        var rows = Enumerable.Range(0, 40).Select(index => (object)new
        {
            id = $"opportunity-{index}", name = $"Negociação {index}", owner = "Diego",
            previousStage = "Proposta", currentStage = "Negociação", value = 12345.67m,
            notes = "Pierre solicitou alteração no botão de WhatsApp; Diego confirmou que o teste funcionou. Preservar a conclusão e os participantes."
        }).ToArray();
        var input = new DailyCheckoutAnalysisInput(new DateOnly(2026, 10, 5),
            new DailyCheckoutSettingsSnapshot("company", "18:00", "America/Sao_Paulo", false),
            new { openedToday = 40 }, [], new { activityChannels = new[] { new { label = "WhatsApp", value = 20 } } },
            new { movements = rows, focus = rows.Take(20).ToArray(), lowEffectiveness = rows.Take(8).ToArray() },
            rows.Take(30).ToArray(), rows.Take(10).ToArray(), rows.Take(8).ToArray());
        var original = JsonSerializer.SerializeToNode(input, JsonOptions)!;

        var compact = PromptContextCompaction.DailyCheckout(input);
        var compactRoot = JsonNode.Parse(compact.Json)!;
        var restored = ExpandReferences(compactRoot, compactRoot);

        Assert.True(JsonNode.DeepEquals(original, restored));
        Assert.True(JsonNode.DeepEquals(original, JsonSerializer.SerializeToNode(input, JsonOptions)));
        Assert.Equal(48, compact.ReferenceCount);
        Assert.True(compact.Json.Length < compact.OriginalCharacters * .8);
        output.WriteLine($"Checkout fixture: {compact.OriginalCharacters} -> {compact.Json.Length} characters; {compact.ReferenceCount} references.");
    }

    [Fact]
    public void Checkout_keeps_rows_with_different_facts_even_when_the_opportunity_id_matches()
    {
        var input = new DailyCheckoutAnalysisInput(new DateOnly(2026, 10, 5),
            new DailyCheckoutSettingsSnapshot(null, "18:00", "America/Sao_Paulo", false), new { }, [], new { },
            new { focus = new[] { new { id = "same-id", notes = "Diego confirmou solução." } } }, [],
            [new { id = "same-id", notes = "Pierre informou que o problema persiste." }], []);

        var compact = PromptContextCompaction.DailyCheckout(input);

        Assert.Equal(0, compact.ReferenceCount);
        Assert.Contains("persiste", compact.Json);
        Assert.DoesNotContain("$ref", compact.Json);
    }

    [Fact]
    public void Verification_compaction_preserves_direction_dates_and_duplicate_evidence_records()
    {
        var input = VerificationInput();
        var original = JsonSerializer.SerializeToNode(input, JsonOptions)!;
        var compact = PromptContextCompaction.Verification(input);
        var root = JsonNode.Parse(compact.Json)!;
        var restored = ExpandReferences(root, root);
        foreach (var item in restored["evidence"]!.AsArray())
            item!["id"] = compact.EvidenceIds[item["id"]!.GetValue<string>()];

        Assert.True(JsonNode.DeepEquals(original, restored));
        Assert.Equal(3, restored["evidence"]!.AsArray().Count);
        Assert.True(compact.Json.Length < compact.OriginalCharacters);
        Assert.Equal(2, compact.ReferenceCount);
        output.WriteLine($"Verification fixture: {compact.OriginalCharacters} -> {compact.Json.Length} characters; {compact.ReferenceCount} references.");
    }

    [Fact]
    public async Task Verification_restores_real_ids_and_records_request_savings()
    {
        var input = VerificationInput();
        var handler = new ResponsesHandler(new SuggestionCompletionVerificationResult("fulfilled", 95, "A equipe confirmou a solução.", ["e2"]));
        var logs = new CapturingLogStore();
        var client = new OpenAiSuggestionCompletionVerificationClient(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), logs);

        var result = await client.AnalyzeAsync(Settings(), input, AiAgentInvocationContext.Unknown, CancellationToken.None);

        Assert.Equal(input.Evidence.ElementAt(1).Id, Assert.Single(result.EvidenceIds));
        Assert.Single(handler.Requests);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("compact", entry.Context.Metadata!["contextMode"]);
        Assert.True((int)entry.Context.Metadata["inputCharactersAfter"]! < (int)entry.Context.Metadata["inputCharactersBefore"]!);
        Assert.Equal(1234, entry.Usage.PromptTokens);
    }

    [Theory]
    [InlineData("fulfilled", 95, "e999")]
    [InlineData("fulfilled", 95, "e1")]
    [InlineData("inconclusive", 90, "e2")]
    [InlineData("unfulfilled", 40, "e2")]
    public async Task Ambiguous_or_unsupported_compact_results_retry_with_all_original_evidence(string status, int confidence, string id)
    {
        var input = VerificationInput();
        var expectedId = input.Evidence.ElementAt(1).Id;
        var handler = new ResponsesHandler(new SuggestionCompletionVerificationResult(status, confidence, "Primeira avaliação.", [id]),
            new SuggestionCompletionVerificationResult("fulfilled", 95, "Execução comprovada.", [expectedId]));
        var logs = new CapturingLogStore();
        var client = new OpenAiSuggestionCompletionVerificationClient(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), logs);

        var result = await client.AnalyzeAsync(Settings(), input, AiAgentInvocationContext.Unknown, CancellationToken.None);

        Assert.Equal("fulfilled", result.Result);
        Assert.Equal(expectedId, Assert.Single(result.EvidenceIds));
        Assert.Equal(2, handler.Requests.Count);
        using var fullRequest = JsonDocument.Parse(handler.Requests[1]);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input, JsonOptions),
            JsonNode.Parse(fullRequest.RootElement.GetProperty("input").GetString()!)));
        Assert.Equal("full-fallback", logs.Entries[1].Context.Metadata!["contextMode"]);
    }

    [Fact]
    public async Task Full_fallback_cannot_mark_fulfilled_using_an_invented_id()
    {
        var handler = new ResponsesHandler(new SuggestionCompletionVerificationResult("fulfilled", 95, "Sem prova.", ["e999"]),
            new SuggestionCompletionVerificationResult("fulfilled", 95, "Sem prova.", ["invented-id"]));
        var client = new OpenAiSuggestionCompletionVerificationClient(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), new CapturingLogStore());

        var result = await client.AnalyzeAsync(Settings(), VerificationInput(), AiAgentInvocationContext.Unknown, CancellationToken.None);

        Assert.Equal("inconclusive", result.Result);
        Assert.True(result.Confidence < 80);
        Assert.Empty(result.EvidenceIds);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Small_context_uses_original_format_when_compaction_instructions_would_cost_more()
    {
        var input = VerificationInput() with
        {
            Description = "Validar botão", Payload = JsonSerializer.SerializeToElement(new { }),
            Evidence = [new("known", "note", DateTime.UtcNow, false, "Ainda pendente.")]
        };
        var handler = new ResponsesHandler(new SuggestionCompletionVerificationResult("unfulfilled", 90, "Pendente.", ["known"]));
        var logs = new CapturingLogStore();
        var client = new OpenAiSuggestionCompletionVerificationClient(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), logs);

        await client.AnalyzeAsync(Settings(), input, AiAgentInvocationContext.Unknown, CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal(Settings().Instructions, request.RootElement.GetProperty("instructions").GetString());
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input, JsonOptions), JsonNode.Parse(request.RootElement.GetProperty("input").GetString()!)));
        Assert.Equal("original", Assert.Single(logs.Entries).Context.Metadata!["contextMode"]);
    }

    [Fact]
    public async Task Checkout_client_sends_compacted_context_and_logs_the_actual_provider_usage()
    {
        var input = new DailyCheckoutAnalysisInput(new DateOnly(2026, 10, 5),
            new DailyCheckoutSettingsSnapshot(null, "18:00", "America/Sao_Paulo", false), new { }, [], new { },
            new { focus = new[] { new { id = "id", notes = new string('x', 400) } } }, [],
            [new { id = "id", notes = new string('x', 400) }], []);
        var handler = new ResponsesHandler(new OpenAiDailyCheckoutResponse(new("Fechamento", "Priorizar retorno"), [], []));
        var logs = new CapturingLogStore();
        var client = new OpenAiResponsesDailyCheckoutClient(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions()), logs);

        await client.AnalyzeAsync(Settings(), input, AiAgentInvocationContext.Unknown, CancellationToken.None);

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        var requestInput = JsonNode.Parse(request.RootElement.GetProperty("input").GetString()!)!;
        Assert.Equal("#/tables/focus/0", requestInput["riskItems"]![0]!["$ref"]!.GetValue<string>());
        Assert.Contains("Formato compacto", request.RootElement.GetProperty("instructions").GetString());
        Assert.Equal(1234, Assert.Single(logs.Entries).Usage.PromptTokens);
    }

    internal static SuggestionCompletionVerificationInput VerificationInput()
    {
        var notes = "Validar com Jean Pierre a alteração do código do botão de WhatsApp, sem atribuir a Pierre uma mensagem enviada por Diego.";
        var post = "outgoing | text | Diego: agora foi, o botão funcionou e a alteração foi validada. Pierre confirmou que não existe outra pendência sobre esse botão. O teste incluiu a página inicial, a versão móvel e a abertura da conversa no número correto. O novo código de rastreamento registrou o clique. Não houve pedido para Diego trocar o código novamente ou responder a si mesmo. A confirmação de Pierre refere-se somente a esse botão, sem concluir a outra pendência de divulgação do telefone 0800.";
        var at = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        return new(Guid.NewGuid().ToString(), "activity", "Validar botão com Pierre", notes, at, at.AddHours(2),
            JsonSerializer.SerializeToElement(new { notes, channel = "whatsapp", requiresSellerResponse = false }),
            [new($"whatsapp:{Guid.NewGuid()}", "whatsapp_message", at.AddMinutes(-2), true, "incoming | text | Pierre: Diego, testa o botão."),
             new($"whatsapp:{Guid.NewGuid()}", "whatsapp_message", at.AddMinutes(1), false, post),
             new($"note:{Guid.NewGuid()}", "note", at.AddMinutes(2), false, post)]);
    }

    internal static AiAgentRuntimeSettings Settings() => new("verification", true, "openai", "gpt-4.1-mini", "test-key", "Verifique o cumprimento com evidências.", 5, null, ["whatsapp_messages", "notes"]);

    private static JsonNode ExpandReferences(JsonNode node, JsonNode root)
    {
        if (node is JsonObject obj)
        {
            if (obj.Count == 1 && obj["$ref"] is JsonValue reference)
            {
                JsonNode target = root;
                foreach (var segment in reference.GetValue<string>()[2..].Split('/'))
                    target = target is JsonArray array ? array[int.Parse(segment)]! : target[segment]!;
                return ExpandReferences(target, root);
            }
            var expanded = new JsonObject();
            foreach (var field in obj) expanded[field.Key] = field.Value is null ? null : ExpandReferences(field.Value, root);
            return expanded;
        }
        if (node is JsonArray rows)
        {
            var expanded = new JsonArray();
            foreach (var row in rows) expanded.Add(row is null ? null : ExpandReferences(row, root));
            return expanded;
        }
        return node.DeepClone();
    }

    private sealed class ResponsesHandler(params object[] results) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var result = results[Math.Min(Requests.Count - 1, results.Length - 1)];
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    usage = new { input_tokens = 1234, output_tokens = 30 },
                    output = new[] { new { content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(result, JsonOptions) } } } }
                }))
            };
        }
    }

    private sealed class CapturingLogStore : IAiAgentInvocationLogStore
    {
        public List<AiAgentInvocationLogEntry> Entries { get; } = [];
        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
