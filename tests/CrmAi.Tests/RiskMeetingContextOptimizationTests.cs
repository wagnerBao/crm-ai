using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace CrmAi.Tests;

public sealed class RiskMeetingContextOptimizationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Request = "Pierre: Diego, valide o código do botão de WhatsApp até amanhã.";
    private const string Confirmation = "Diego: agora funcionou; validei o botão e não existe mais esse problema.";

    [Fact]
    public void Stored_summary_is_bound_to_the_exact_transcript_and_preserves_original_analysis_json()
    {
        var meeting = Meeting();
        var analysis = new OpenAiMeetingAudioAnalysisResponse("Resumo", [], [], "Confirmar teste",
            RiskContext: meeting.RiskContext!.Context);
        var json = PostgresMeetingAudioAnalysisService.SerializeAnalysisWithRiskContext(analysis, meeting.Transcript);
        var stored = MeetingRiskContextPolicy.Read(json);

        Assert.True(MeetingRiskContextPolicy.CanReuse(stored, meeting.Transcript));
        Assert.False(MeetingRiskContextPolicy.CanReuse(stored, meeting.Transcript + " Pierre: o problema voltou."));
        Assert.Equal("Resumo", JsonNode.Parse(json)!["summary"]!.GetValue<string>());
        Assert.Null(MeetingRiskContextPolicy.Read("{broken"));
        Assert.Null(MeetingRiskContextPolicy.Read("{}"));
        Assert.Null(MeetingRiskContextPolicy.Read("[]"));
        Assert.Null(MeetingRiskContextPolicy.Read("null"));
        Assert.False(MeetingRiskContextPolicy.CanReuse(stored! with { Version = "old" }, meeting.Transcript));
    }

    [Theory]
    [InlineData("invented")]
    [InlineData("uncertain")]
    [InlineData("incomplete")]
    [InlineData("empty")]
    [InlineData("bad-date")]
    public void Invalid_or_uncertain_summaries_are_not_reused(string kind)
    {
        var meeting = Meeting();
        var context = meeting.RiskContext!.Context;
        context = kind switch
        {
            "invented" => context with { Facts = [context.Facts.First() with { EvidenceExcerpt = "Uma frase que nunca foi dita." }] },
            "uncertain" => context with { ConfidenceScore = 84 },
            "incomplete" => context with { NeedsFullTranscript = true },
            "empty" => context with { Facts = [] },
            _ => context with { Facts = [context.Facts.First() with { DueAt = "inventado" }] }
        };
        Assert.Null(MeetingRiskContextPolicy.Bind(context, meeting.Transcript));
        var input = Input(meeting with { RiskContext = meeting.RiskContext with { Context = context } });
        Assert.False(RiskMeetingContextOptimization.Build(input).SavesCharacters);
    }

    [Fact]
    public void Compact_context_preserves_actors_dates_negative_confirmation_and_other_blocks_without_mutation()
    {
        var input = Input(Meeting());
        var original = JsonSerializer.Serialize(input, JsonOptions);
        var prompt = RiskMeetingContextOptimization.Build(input);
        var full = JsonNode.Parse(prompt.FullJson)!;
        var compact = JsonNode.Parse(prompt.CompactJson)!;

        Assert.True(prompt.SavesCharacters);
        Assert.Equal(1, prompt.ReusedMeetings);
        Assert.Equal(new[] { "m1:f1", "m1:f2" }, prompt.EvidenceIds);
        Assert.DoesNotContain("transcript", compact["meetingAudioAnalyses"]![0]!.AsObject().Select(pair => pair.Key));
        var facts = compact["meetingAudioAnalyses"]![0]!["riskContext"]!["facts"]!.AsArray();
        Assert.Equal("Pierre", facts[0]!["participant"]!.GetValue<string>());
        Assert.Equal("Diego", facts[1]!["participant"]!.GetValue<string>());
        Assert.Equal("2026-10-06T15:00:00Z", facts[0]!["dueAt"]!.GetValue<string>());
        Assert.Equal(Confirmation, facts[1]!["evidenceExcerpt"]!.GetValue<string>());
        var span = facts[1]!["spans"]![0]!;
        Assert.True(span["startChar"]!.GetValue<int>() > 6000);
        Assert.Contains(Confirmation, span["text"]!.GetValue<string>());
        foreach (var field in full.AsObject().Select(pair => pair.Key).Where(key => key != "meetingAudioAnalyses"))
            Assert.True(JsonNode.DeepEquals(full[field], compact[field]));
        Assert.Equal(original, JsonSerializer.Serialize(input, JsonOptions));
        output.WriteLine($"Meeting context fixture: {prompt.FullJson.Length} -> {prompt.CompactJson.Length} characters (before extra instructions).");
    }

    [Fact]
    public void Legacy_or_edited_transcript_keeps_all_text_including_the_final_resolution()
    {
        var meeting = Meeting();
        foreach (var changed in new[] { meeting with { RiskContext = null }, meeting with { Transcript = meeting.Transcript + " Pierre: voltou a falhar." } })
        {
            var prompt = RiskMeetingContextOptimization.Build(Input(changed));
            Assert.False(prompt.SavesCharacters);
            Assert.Equal(changed.Transcript, JsonNode.Parse(prompt.FullJson)!["meetingAudioAnalyses"]![0]!["transcript"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Repeated_quotes_preserve_all_occurrences_and_neighboring_speakers()
    {
        var meeting = Meeting();
        var transcript = meeting.Transcript + "\nPierre: outro teste. " + Confirmation;
        var prompt = RiskMeetingContextOptimization.Build(Input(meeting with
        {
            Transcript = transcript,
            RiskContext = MeetingRiskContextPolicy.Bind(meeting.RiskContext!.Context, transcript)
        }));
        var spans = JsonNode.Parse(prompt.CompactJson)!["meetingAudioAnalyses"]![0]!["riskContext"]!["facts"]![1]!["spans"]!.AsArray();
        Assert.Equal(2, spans.Count);
        Assert.Contains("Pierre: outro teste", spans[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Short_meetings_or_dense_evidence_keep_the_original_input_when_compaction_would_cost_more()
    {
        var meeting = Meeting();
        var transcript = Request + "\n" + Confirmation;
        var prompt = RiskMeetingContextOptimization.Build(Input(meeting with
        {
            Transcript = transcript,
            RiskContext = MeetingRiskContextPolicy.Bind(meeting.RiskContext!.Context, transcript)
        }));
        Assert.False(prompt.SavesCharacters);
        Assert.Equal(prompt.FullJson, prompt.CompactJson);
    }

    [Fact]
    public void Every_compacted_meeting_requires_a_valid_evidence_citation()
    {
        var prompt = RiskMeetingContextOptimization.Build(Input(Meeting(), Meeting() with { ActivityId = "other" }));
        Assert.Equal(2, prompt.ReusedMeetings);
        Assert.False(prompt.Supports(CompactResult()));
        Assert.True(prompt.Supports(CompactResult() with { MeetingEvidenceIds = ["m1:f2", "m2:f2"] }));
    }

    [Fact]
    public async Task Default_mode_uses_one_full_call_without_enabling_unvalidated_compaction()
    {
        var handler = new ResponsesHandler(FullResult());
        var logs = new LogStore();
        await Client(handler, logs, new()).AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);
        Assert.Single(handler.Requests);
        Assert.NotNull(RequestInput(handler.Requests[0])["meetingAudioAnalyses"]![0]!["transcript"]);
        Assert.Equal("original", Assert.Single(logs.Entries).Context.Metadata!["contextMode"]);
    }

    [Fact]
    public async Task Companies_outside_the_pilot_keep_one_full_call()
    {
        var handler = new ResponsesHandler(FullResult());
        var logs = new LogStore();
        await Client(handler, logs, new() { RiskMeetingContextMode = "shadow", RiskMeetingContextCompanyIds = ["another-company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);
        Assert.Single(handler.Requests);
        Assert.Equal("original", Assert.Single(logs.Entries).Context.Metadata!["contextMode"]);
    }

    [Fact]
    public async Task Compact_mode_reuses_the_summary_with_one_call_and_logs_real_usage()
    {
        var handler = new ResponsesHandler(CompactResult());
        var logs = new LogStore();
        var result = await Client(handler, logs, new() { RiskMeetingContextMode = "compact", RiskMeetingContextCompanyIds = ["company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);

        Assert.Equal("LOW", result.RiskLevel);
        Assert.Single(handler.Requests);
        Assert.NotNull(RequestInput(handler.Requests[0])["meetingAudioAnalyses"]![0]!["riskContext"]);
        var instructions = JsonNode.Parse(handler.Requests[0])!["instructions"]!.GetValue<string>();
        Assert.Contains("users sao usuarios internos", instructions);
        Assert.Contains("contacts sao contatos externos", instructions);
        Assert.Contains("needsFullMeetingContext", instructions);
        var log = Assert.Single(logs.Entries);
        Assert.Equal(1200, log.Usage.PromptTokens);
        Assert.Equal("compact", log.Context.Metadata!["contextMode"]);
    }

    [Theory]
    [InlineData("low-confidence")]
    [InlineData("needs-full")]
    [InlineData("invented-id")]
    [InlineData("no-citations")]
    [InlineData("missing-confidence")]
    public async Task Ambiguous_compact_result_causes_exactly_one_full_fallback(string issue)
    {
        var response = issue switch
        {
            "low-confidence" => CompactResult() with { ContextConfidenceScore = 84 },
            "needs-full" => CompactResult() with { NeedsFullMeetingContext = true },
            "invented-id" => CompactResult() with { MeetingEvidenceIds = ["m99:f1"] },
            "no-citations" => CompactResult() with { MeetingEvidenceIds = [] },
            _ => CompactResult() with { ContextConfidenceScore = null }
        };
        var handler = new ResponsesHandler(response, FullResult());
        var logs = new LogStore();
        var result = await Client(handler, logs, new() { RiskMeetingContextMode = "compact", RiskMeetingContextCompanyIds = ["company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);

        Assert.Equal(FullResult().RiskLevel, result.RiskLevel);
        Assert.Equal(FullResult().RiskScore, result.RiskScore);
        Assert.Equal(FullResult().Reasons, result.Reasons);
        Assert.Equal(FullResult().Recommendations, result.Recommendations);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Meeting().Transcript, RequestInput(handler.Requests[1])["meetingAudioAnalyses"]![0]!["transcript"]!.GetValue<string>());
        Assert.Equal("full-fallback", logs.Entries[1].Context.Metadata!["contextMode"]);
        Assert.Equal(logs.Entries[0].Context.Metadata!["riskContextComparisonId"], logs.Entries[1].Context.Metadata!["riskContextComparisonId"]);
        Assert.Equal(2400, logs.Entries.Sum(entry => entry.Usage.PromptTokens));
    }

    [Fact]
    public async Task Shadow_returns_the_full_result_and_logs_divergence_for_review()
    {
        var handler = new ResponsesHandler(FullResult(), CompactResult());
        var logs = new LogStore();
        var result = await Client(handler, logs, new() { RiskMeetingContextMode = "shadow", RiskMeetingContextCompanyIds = ["company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);

        Assert.Equal(FullResult().RiskLevel, result.RiskLevel);
        Assert.Equal(FullResult().RiskScore, result.RiskScore);
        Assert.Equal(FullResult().Reasons, result.Reasons);
        Assert.Equal(FullResult().Recommendations, result.Recommendations);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("shadow-full", logs.Entries[0].Context.Metadata!["contextMode"]);
        Assert.Equal("shadow-compact", logs.Entries[1].Context.Metadata!["contextMode"]);
        Assert.Equal(false, logs.Entries[1].Context.Metadata!["riskLevelAgreement"]);
        Assert.Equal(-55, logs.Entries[1].Context.Metadata!["riskScoreDelta"]);
        Assert.NotNull(RequestInput(handler.Requests[0])["meetingAudioAnalyses"]![0]!["transcript"]);
    }

    [Fact]
    public async Task Shadow_comparison_failure_cannot_discard_the_full_result()
    {
        var handler = new ResponsesHandler(FullResult()) { FailSecondCall = true };
        var result = await Client(handler, new LogStore(), new() { RiskMeetingContextMode = "shadow", RiskMeetingContextCompanyIds = ["company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);
        Assert.Equal(FullResult().RiskLevel, result.RiskLevel);
        Assert.Equal(FullResult().RiskScore, result.RiskScore);
        Assert.Equal(FullResult().Reasons, result.Reasons);
        Assert.Equal(FullResult().Recommendations, result.Recommendations);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Compact_http_error_does_not_retry_or_hide_api_failures()
    {
        var handler = new ResponsesHandler(CompactResult()) { FailFirstCall = true };
        await Assert.ThrowsAsync<OpenAiRequestException>(() => Client(handler, new LogStore(), new() { RiskMeetingContextMode = "compact", RiskMeetingContextCompanyIds = ["company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Malformed_compact_output_recovers_the_full_context_once()
    {
        var handler = new ResponsesHandler(CompactResult(), FullResult()) { MalformedFirstResult = true };
        var logs = new LogStore();
        var result = await Client(handler, logs, new() { RiskMeetingContextMode = "compact", RiskMeetingContextCompanyIds = ["company"] })
            .AnalyzeAsync(Settings(), Input(Meeting()), AiAgentInvocationContext.Unknown with { CompanyId = "company" }, default);
        Assert.Equal(FullResult().RiskLevel, result.RiskLevel);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(logs.Entries[0].Success);
        Assert.Equal("full-fallback", logs.Entries[1].Context.Metadata!["contextMode"]);
    }

    private static AnalysisMeetingAudioSummary Meeting()
    {
        var transcript = Request + "\n" + string.Concat(Enumerable.Repeat("Conversa sobre apresentação e organização da reunião.\n", 180)) + Confirmation;
        var context = new MeetingRiskContext([
            new("commitment", "Pierre solicita a Diego validar o botão até amanhã.", "Pierre", "2026-10-06T15:00:00Z", Request),
            new("resolution", "Diego confirmou que o teste funcionou; o problema foi resolvido.", "Diego", null, Confirmation)
        ], 95, false);
        var now = new DateTime(2026, 10, 5, 15, 0, 0, DateTimeKind.Utc);
        return new("activity", transcript, "Resumo completo do teste e resolução", now, now, MeetingRiskContextPolicy.Bind(context, transcript));
    }

    private static RiskAnalysisAgentInput Input(params AnalysisMeetingAudioSummary[] meetings) => new(
        null, null, [], new("pipeline", "stage", "Negociação", 2, null, 5, false), new(1, 0, []),
        [new("Pierre confirmou por WhatsApp que a troca funcionou.", "user-diego", DateTime.UtcNow)],
        [new("Pierre", "Cliente", "active", "user-diego", "contact-pierre")],
        [new("Diego", "Comercial", true, "user-diego")], [], [],
        new("activity.updated", DateTime.UtcNow, "user-diego"), new([], 100, 95), meetings);

    private static OpenAiRiskAnalysisResponse FullResult() => new("MEDIUM", 60, ["Revisar os demais compromissos."], ["Confirmar prazos com Pierre."]);
    private static OpenAiRiskAnalysisResponse CompactResult() => new("LOW", 5, ["Diego confirmou a resolução do problema do botão."], ["Acompanhar os demais compromissos com Pierre."], 95, false, ["m1:f1", "m1:f2"]);
    private static AiAgentRuntimeSettings Settings() => new("risk-analysis", true, "openai", "gpt-4.1-mini", "test-key", "Avalie o risco.", 5, null, ["activities", "contacts", "users"]);
    private static JsonNode RequestInput(string request) => JsonNode.Parse(JsonNode.Parse(request)!["input"]!.GetValue<string>())!;
    private static OpenAiResponsesRiskAnalysisClient Client(ResponsesHandler handler, LogStore logs, OpenAiRiskAnalysisOptions options) => new(new HttpClient(handler), Options.Create(options), logs);

    private sealed class LogStore : IAiAgentInvocationLogStore
    {
        public List<AiAgentInvocationLogEntry> Entries { get; } = [];
        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken) { Entries.Add(entry); return Task.CompletedTask; }
    }

    private sealed class ResponsesHandler(params OpenAiRiskAnalysisResponse[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool FailFirstCall { get; init; }
        public bool FailSecondCall { get; init; }
        public bool MalformedFirstResult { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if ((Requests.Count == 1 && FailFirstCall) || (Requests.Count == 2 && FailSecondCall))
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"unavailable\"}") };
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    output = new[] { new { content = new[] { new { type = "output_text", text = Requests.Count == 1 && MalformedFirstResult ? "{broken" : JsonSerializer.Serialize(responses[Requests.Count - 1], JsonOptions) } } } },
                    usage = new { input_tokens = 1200, output_tokens = 100, total_tokens = 1300 }
                }, JsonOptions))
            };
        }
    }
}
