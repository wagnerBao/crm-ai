using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrmAi.Application;
using CrmAi.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace CrmAi.Tests;

public sealed class SuggestionEvidenceSelectionTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly DateTime CreatedAt = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AiAgentInvocationContext Context = new("verification", CompanyId: "pilot");

    [Fact]
    public void Select_keeps_requests_participants_deadlines_short_confirmations_and_contradictions()
    {
        var input = Input();
        var original = JsonSerializer.Serialize(input, JsonOptions);
        var selection = SuggestionEvidenceSelector.Select(input);

        Assert.True(selection.HasSelection);
        foreach (var id in new[] { "request", "topic", "short-confirmation", "contradiction", "final-confirmation", "deadline", "activity", "note", "history", "meeting", "opportunity" })
            Assert.Contains(selection.Selected.Evidence, item => item.Id == id);
        Assert.Contains(selection.Selected.Evidence, item => item.Id == "topic" && item.Summary.Contains("Pierre"));
        Assert.Contains(selection.Selected.Evidence, item => item.Id == "final-confirmation" && item.Summary.Contains("Diego"));
        Assert.Equal(input.CreatedAt, selection.Selected.CreatedAt);
        Assert.Equal(input.DueAt, selection.Selected.DueAt);
        Assert.Equal(input.Payload.GetRawText(), selection.Selected.Payload.GetRawText());
        foreach (var kept in selection.Selected.Evidence) Assert.Same(input.Evidence.Single(item => item.Id == kept.Id), kept);
        Assert.Equal(input.Evidence.Where(item => !selection.OmittedIds.Contains(item.Id)).Select(item => item.Id),
            selection.Selected.Evidence.Select(item => item.Id));
        Assert.Equal(original, JsonSerializer.Serialize(input, JsonOptions));
        var before = JsonSerializer.Serialize(input, JsonOptions).Length;
        var after = JsonSerializer.Serialize(selection.Selected, JsonOptions).Length;
        Assert.True(after < before * .7);
        output.WriteLine($"Evidence fixture: {input.Evidence.Count} -> {selection.Selected.Evidence.Count} records; {before} -> {after} characters before extra instructions.");
    }

    [Fact]
    public void Select_preserves_the_entire_episode_around_a_relevant_or_ambiguous_message()
    {
        var input = Input();
        var unrelated = input.Evidence.Single(item => item.Id == "unrelated-5");
        input = input with
        {
            Evidence = input.Evidence.Append(new SuggestionCompletionEvidence("episode-anchor", "whatsapp_message",
                unrelated.OccurredAt.AddMinutes(2), false, "incoming | text | Pierre: a troca do código do botão de WhatsApp está pendente.", "whatsapp:main")).ToArray()
        };
        var selection = SuggestionEvidenceSelector.Select(input);
        Assert.Contains(selection.Selected.Evidence, item => item.Id == unrelated.Id);
        Assert.Contains(selection.Selected.Evidence, item => item.Id == "episode-anchor");
        Assert.Contains(selection.OmittedIds, id => id == "unrelated-6");
    }

    [Theory]
    [InlineData("incoming | text | Pierre: não funcionou depois da troca.")]
    [InlineData("outgoing | text | Diego: agora foi")]
    [InlineData("incoming | text | 👍")]
    [InlineData("incoming | text | O número 0800 final 2120 aparece em várias páginas comerciais da empresa.")]
    [InlineData("outgoing | text | Enviei o contrato e aguardo o retorno.")]
    [InlineData("incoming | audio | AudioMessage")]
    [InlineData("unknown | text | Conteúdo comercial em um formato cujo participante não foi identificado corretamente.")]
    public void Safety_signals_and_unknown_formats_are_never_pruned(string summary)
    {
        var input = Input();
        var replacement = input.Evidence.Single(item => item.Id == "unrelated-8") with { Summary = summary };
        input = input with { Evidence = input.Evidence.Select(item => item.Id == replacement.Id ? replacement : item).ToArray() };
        Assert.Contains(SuggestionEvidenceSelector.Select(input).Selected.Evidence, item => item.Id == replacement.Id);
    }

    [Fact]
    public void Truncated_messages_unknown_streams_and_generic_actions_keep_the_full_pool()
    {
        var input = Input();
        var unknown = input with { Evidence = input.Evidence.Select(item => item with { SourceStreamId = null }).ToArray() };
        Assert.False(SuggestionEvidenceSelector.Select(unknown).HasSelection);
        var generic = input with
        {
            Title = "Responder a Diego", Description = "Confirmar resposta com Pierre",
            Payload = JsonSerializer.SerializeToElement(new { ownerUserName = "Diego", contactName = "Pierre" })
        };
        Assert.False(SuggestionEvidenceSelector.Select(generic).HasSelection);
        var replacement = input.Evidence.Single(item => item.Id == "unrelated-8") with { Summary = new string('x', 1200) };
        Assert.Contains(SuggestionEvidenceSelector.Select(input with
        {
            Evidence = input.Evidence.Select(item => item.Id == replacement.Id ? replacement : item).ToArray()
        }).Selected.Evidence, item => item.Id == replacement.Id);
    }

    [Fact]
    public void Episodes_do_not_cross_conversations_or_channels()
    {
        var input = Input();
        var unrelated = input.Evidence.Single(item => item.Id == "unrelated-5");
        var extras = new[]
        {
            new SuggestionCompletionEvidence("other-first", "whatsapp_message", unrelated.OccurredAt.AddHours(-1), false, "incoming | text | Oi", "whatsapp:other"),
            new SuggestionCompletionEvidence("other-anchor", "whatsapp_message", unrelated.OccurredAt, false, "incoming | text | Pierre: validar o botão.", "whatsapp:other"),
            new SuggestionCompletionEvidence("other-last", "whatsapp_message", unrelated.OccurredAt.AddHours(1), false, "outgoing | text | Certo", "whatsapp:other")
        };
        var selection = SuggestionEvidenceSelector.Select(input with { Evidence = input.Evidence.Concat(extras).ToArray() });
        Assert.Contains(selection.OmittedIds, id => id == unrelated.Id);
        Assert.Contains(selection.Selected.Evidence, item => item.Id == "other-anchor");
    }

    [Fact]
    public void Cache_includes_omitted_evidence_and_the_effective_policy_mode()
    {
        var input = Input();
        var selection = SuggestionEvidenceSelector.Select(input);
        var id = selection.OmittedIds.First();
        var changed = input with
        {
            Evidence = input.Evidence.Select(item => item.Id == id ? item with { Summary = item.Summary + " Detalhes decorativos institucionais." } : item).ToArray()
        };
        Assert.Equal(selection.Selected.Evidence.Select(item => item.Id), SuggestionEvidenceSelector.Select(changed).Selected.Evidence.Select(item => item.Id));
        var fingerprint = SuggestionVerificationCache.Fingerprint(input, Settings(), "model", CreatedAt.AddDays(2), "selected");
        Assert.NotEqual(fingerprint, SuggestionVerificationCache.Fingerprint(changed, Settings(), "model", CreatedAt.AddDays(2), "selected"));
        Assert.NotEqual(fingerprint, SuggestionVerificationCache.Fingerprint(input, Settings(), "model", CreatedAt.AddDays(2), "shadow"));
        Assert.NotEqual(fingerprint, SuggestionVerificationCache.Fingerprint(input, Settings(), "model", CreatedAt.AddDays(2), "full"));
        Assert.NotEqual(fingerprint, SuggestionVerificationCache.Fingerprint(input with
        {
            Evidence = input.Evidence.Select(item => item.Id == id ? item with { SourceStreamId = "another-conversation" } : item).ToArray()
        }, Settings(), "model", CreatedAt.AddDays(2), "selected"));
    }

    [Fact]
    public async Task Selected_mode_sends_the_subset_restores_real_ids_and_logs_all_api_usage()
    {
        var input = Input();
        var handler = new Handler(input, selected => Success(selected, "final-confirmation"));
        var logs = new LogStore();
        var result = await Client(handler, logs, "selected").AnalyzeAsync(Settings(), input, Context, default);
        Assert.Equal("fulfilled", result.Result);
        Assert.Equal("final-confirmation", Assert.Single(result.EvidenceIds));
        var request = Assert.Single(handler.Requests);
        Assert.True(RequestInput(request)["evidence"]!.AsArray().Count < input.Evidence.Count);
        Assert.DoesNotContain("whatsapp:main", request);
        Assert.Contains(RequestInput(request)["evidence"]!.AsArray(), row => row!["stream"]?.GetValue<string>() == "c1");
        var requestRoot = JsonNode.Parse(request)!;
        Assert.Contains("outgoing e equipe interna", requestRoot["instructions"]!.GetValue<string>());
        Assert.Contains("needsFullEvidence", requestRoot["text"]!["format"]!["schema"]!["required"]!.ToJsonString());
        Assert.Equal(1200, Assert.Single(logs.Entries).Usage.PromptTokens);
        Assert.Equal("selected", logs.Entries[0].Context.Metadata!["evidenceSelectionRole"]);
        Assert.Equal(true, logs.Entries[0].Context.Metadata!["selectedEvidenceResultSupported"]);
    }

    [Theory]
    [InlineData("needs-full")]
    [InlineData("low-confidence")]
    [InlineData("inconclusive")]
    [InlineData("invented-id")]
    [InlineData("prior-only")]
    [InlineData("missing-flag")]
    [InlineData("negative-without-proof")]
    public async Task Insufficient_subset_results_recover_the_entire_original_pool_once(string issue)
    {
        var input = Input();
        var handler = new Handler(input, selected => issue switch
        {
            "needs-full" => Success(selected, "final-confirmation") with { NeedsFullEvidence = true },
            "low-confidence" => Success(selected, "final-confirmation") with { Confidence = 89 },
            "inconclusive" => Success(selected, "final-confirmation") with { Result = "inconclusive" },
            "invented-id" => Success(selected, "final-confirmation") with { EvidenceIds = ["invented"] },
            "prior-only" => Success(selected, "request"),
            "missing-flag" => Success(selected, "final-confirmation") with { NeedsFullEvidence = null },
            _ => new("unfulfilled", 95, "Ausência não comprova pendência.", [], false)
        });
        var logs = new LogStore();
        var result = await Client(handler, logs, "selected").AnalyzeAsync(Settings(), input, Context, default);
        Assert.Equal("fulfilled", result.Result);
        Assert.Equal("final-confirmation", Assert.Single(result.EvidenceIds));
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(input, JsonOptions), RequestInput(handler.Requests[1])));
        Assert.Equal("full-fallback", logs.Entries[1].Context.Metadata!["evidenceSelectionRole"]);
        Assert.Equal(2400, logs.Entries.Sum(entry => entry.Usage.PromptTokens));
    }

    [Fact]
    public async Task Shadow_returns_the_full_pool_result_and_logs_disagreement_with_physical_ids()
    {
        var input = Input();
        var handler = new Handler(input, selected => Success(selected, "final-confirmation") with
        { Result = "unfulfilled", Reason = "Comparação divergente." });
        var logs = new LogStore();
        var result = await Client(handler, logs, "shadow").AnalyzeAsync(Settings(), input, Context, default);
        Assert.Equal("fulfilled", result.Result);
        Assert.Equal("final-confirmation", Assert.Single(result.EvidenceIds));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(input.Evidence.Count, RequestInput(handler.Requests[0])["evidence"]!.AsArray().Count);
        Assert.Equal(false, logs.Entries[1].Context.Metadata!["verificationResultAgreement"]);
        Assert.Equal(1, logs.Entries[1].Context.Metadata!["verificationEvidenceOverlap"]);
        Assert.Equal(logs.Entries[0].Context.Metadata!["evidenceSelectionComparisonId"], logs.Entries[1].Context.Metadata!["evidenceSelectionComparisonId"]);
    }

    [Fact]
    public async Task Failed_shadow_comparison_cannot_replace_the_authoritative_full_pool_result()
    {
        var input = Input();
        var handler = new Handler(input, selected => Success(selected, "final-confirmation")) { FailSelectedHttp = true };
        var result = await Client(handler, new LogStore(), "shadow").AnalyzeAsync(Settings(), input, Context, default);
        Assert.Equal("fulfilled", result.Result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Selection_http_errors_do_not_start_another_paid_attempt()
    {
        var input = Input();
        var handler = new Handler(input, selected => Success(selected, "final-confirmation")) { FailSelectedHttp = true };
        await Assert.ThrowsAsync<OpenAiRequestException>(() => Client(handler, new LogStore(), "selected").AnalyzeAsync(Settings(), input, Context, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Malformed_selected_output_recovers_full_context_once_and_full_invalid_ids_stay_inconclusive()
    {
        var input = Input();
        var handler = new Handler(input, selected => Success(selected, "final-confirmation")) { MalformedSelected = true, InvalidBaselineIds = true };
        var logs = new LogStore();
        var result = await Client(handler, logs, "selected").AnalyzeAsync(Settings(), input, Context, default);
        Assert.Equal("inconclusive", result.Result);
        Assert.Empty(result.EvidenceIds);
        Assert.True(result.Confidence < 80);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(logs.Entries[0].Success);
    }

    [Fact]
    public async Task Outside_the_pilot_selection_stays_off_and_small_pools_never_add_a_comparison_call()
    {
        var input = Input();
        var handler = new Handler(input, selected => Success(selected, "final-confirmation"));
        await Client(handler, new LogStore(), "selected").AnalyzeAsync(Settings(), input, Context with { CompanyId = "outside" }, default);
        Assert.Single(handler.Requests);
        Assert.Equal(input.Evidence.Count, RequestInput(handler.Requests[0])["evidence"]!.AsArray().Count);
        var small = input with { Evidence = input.Evidence.Where(item => item.Id is "request" or "final-confirmation").ToArray() };
        var smallHandler = new Handler(small, selected => Success(selected, "final-confirmation"));
        await Client(smallHandler, new LogStore(), "shadow").AnalyzeAsync(Settings(), small, Context, default);
        Assert.Single(smallHandler.Requests);
    }

    private static SuggestionCompletionVerificationInput Input()
    {
        var records = new List<SuggestionCompletionEvidence>
        {
            new("request", "whatsapp_message", CreatedAt.AddMinutes(-5), true, "incoming | text | Pierre: Diego, troque o código do botão de WhatsApp e valide o rastreamento.", "whatsapp:main"),
            new("topic", "whatsapp_message", CreatedAt.AddMinutes(2), false, "incoming | text | Pierre: a página usa um código diferente no botão de WhatsApp; preciso da validação.", "whatsapp:main"),
            new("short-confirmation", "whatsapp_message", CreatedAt.AddMinutes(3), false, "outgoing | text | Diego: agora foi", "whatsapp:main")
        };
        var names = new[] { "alfa", "beta", "gama", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron", "pi", "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi", "omega" };
        for (var index = 0; index < names.Length; index++)
        {
            var summary = $"incoming | text | Pierre: A exposição {names[index]} reúne cerâmicas azuis texturas claras luminárias decorativas mobiliário elegante plantas ornamentais e imagens institucionais. "
                + "O catálogo editorial destaca composição visual tipografia fotografias paisagens objetos artísticos identidade estética materiais gráficos cores suaves ilustrações vitrines ambientes espaços culturais e referências de arquitetura.";
            records.Add(new($"unrelated-{index}", "whatsapp_message", CreatedAt.AddMinutes(60 + index * 45), false, summary, "whatsapp:main"));
        }
        records.Add(new("contradiction", "whatsapp_message", CreatedAt.AddHours(7.5), false, "incoming | text | Pierre: não funcionou depois da troca.", "whatsapp:main"));
        records.Add(new("deadline", "whatsapp_message", CreatedAt.AddHours(10), false, "incoming | text | Pierre: validar até amanhã às 12h, o número 0800 final 2120 é outro assunto.", "whatsapp:main"));
        records.Add(new("final-confirmation", "whatsapp_message", CreatedAt.AddHours(21), false, "outgoing | text | Diego: validei o botão, agora funcionou e registrei o clique correto.", "whatsapp:main"));
        foreach (var (id, type) in new[] { ("activity", "activity"), ("note", "note"), ("history", "opportunity_history"), ("meeting", "meeting_recording"), ("opportunity", "opportunity") })
            records.Add(new(id, type, CreatedAt.AddHours(6), false, "Registro do CRM com responsáveis, estado atual, condições e datas que precisam ser mantidos."));
        return new("suggestion", "activity", "Validar código do botão de WhatsApp com Pierre",
            "Diego deve validar a troca do código do botão de WhatsApp e o rastreamento solicitado por Pierre.", CreatedAt, CreatedAt.AddDays(1),
            JsonSerializer.SerializeToElement(new { notes = "Validar botão de WhatsApp", ownerUserName = "Diego", contactName = "Pierre" }), records.OrderByDescending(item => item.OccurredAt).ThenBy(item => item.Id).ToArray());
    }

    private static JsonNode RequestInput(string request) => JsonNode.Parse(JsonNode.Parse(request)!["input"]!.GetValue<string>())!;
    private static SuggestionCompletionVerificationResult Success(JsonNode requestInput, string physicalId)
    {
        // Requests preserve order even when their IDs use local aliases.
        var row = requestInput["evidence"]!.AsArray().Single(item => item!["summary"] is JsonValue value
            && value.TryGetValue<string>(out var summary) && (physicalId switch
            {
                "request" => summary.Contains("troque o código"),
                _ => summary.Contains("registrei o clique correto")
            }));
        return new("fulfilled", 95, "Diego confirmou a validação solicitada por Pierre.", [row!["id"]!.GetValue<string>()], false);
    }
    private static AiAgentRuntimeSettings Settings() => new("suggestion-completion-verification", true, "openai", "model", "test-key", "Verifique com evidências.", 5, null, ["whatsapp_messages", "activities", "notes"]);
    private static OpenAiSuggestionCompletionVerificationClient Client(Handler handler, LogStore logs, string mode) => new(new HttpClient(handler), Options.Create(new OpenAiRiskAnalysisOptions
    { SuggestionEvidenceSelectionMode = mode, SuggestionEvidenceSelectionCompanyIds = ["pilot"] }), logs);

    private sealed class LogStore : IAiAgentInvocationLogStore
    {
        public List<AiAgentInvocationLogEntry> Entries { get; } = [];
        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken) { Entries.Add(entry); return Task.CompletedTask; }
    }

    private sealed class Handler(SuggestionCompletionVerificationInput original, Func<JsonNode, SuggestionCompletionVerificationResult> selectedResponse) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool FailSelectedHttp { get; init; }
        public bool MalformedSelected { get; init; }
        public bool InvalidBaselineIds { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var input = RequestInput(Requests[^1]);
            var selected = input["evidence"]!.AsArray().Count < original.Evidence.Count;
            if (selected && FailSelectedHttp) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            var result = selected ? selectedResponse(input) : InvalidBaselineIds
                ? new SuggestionCompletionVerificationResult("fulfilled", 95, "Id inventado.", ["invented"])
                : Success(input, "final-confirmation") with { NeedsFullEvidence = null };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    output = new object[] { new { type = "reasoning" }, new { content = new[] { new { type = "output_text", text = selected && MalformedSelected ? "{broken" : JsonSerializer.Serialize(result, JsonOptions) } } } },
                    usage = new { input_tokens = 1200, output_tokens = 100, total_tokens = 1300 }
                }, JsonOptions))
            };
        }
    }
}
