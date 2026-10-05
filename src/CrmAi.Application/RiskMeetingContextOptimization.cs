using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrmAi.Application;

internal sealed record RiskMeetingPromptContext(
    string FullJson,
    string CompactJson,
    IReadOnlyCollection<string> EvidenceIds,
    IReadOnlyCollection<string> CompactedMeetingPrefixes,
    int ReusedMeetings)
{
    public bool SavesCharacters => ReusedMeetings > 0
        && CompactJson.Length + RiskMeetingContextOptimization.CompactInstructions.Length + 2 < FullJson.Length;

    public bool Supports(OpenAiRiskAnalysisResponse response) =>
        response is { ContextConfidenceScore: >= 85 and <= 100, NeedsFullMeetingContext: false,
            RiskScore: >= 0 and <= 100, Reasons.Count: > 0, Recommendations.Count: > 0 }
        && new[] { "LOW", "MEDIUM", "HIGH" }.Contains(response.RiskLevel)
        && response.MeetingEvidenceIds is { Count: > 0 }
        && response.MeetingEvidenceIds.All(id => EvidenceIds.Contains(id, StringComparer.Ordinal))
        && CompactedMeetingPrefixes.All(prefix => response.MeetingEvidenceIds.Any(id => id.StartsWith(prefix, StringComparison.Ordinal)));

    public AiAgentInvocationContext WithMetrics(AiAgentInvocationContext context, string mode, string comparisonId) => context with
    {
        Metadata = new Dictionary<string, object?>(context.Metadata ?? new Dictionary<string, object?>())
        {
            ["contextOptimizationVersion"] = RiskMeetingContextOptimization.Version,
            ["contextMode"] = mode,
            ["riskContextComparisonId"] = comparisonId,
            ["inputCharactersBefore"] = FullJson.Length,
            ["inputCharactersAfter"] = mode is "compact" or "shadow-compact" ? CompactJson.Length : FullJson.Length,
            ["addedInstructionCharacters"] = RiskMeetingContextOptimization.ParticipantInstructions.Length + 2
                + (mode is "compact" or "shadow-compact" ? RiskMeetingContextOptimization.CompactInstructions.Length + 2 : 0),
            ["reusedMeetingSummaries"] = ReusedMeetings
        }
    };
}

internal static class RiskMeetingContextOptimization
{
    public const string Version = "risk-meeting-context-v1";
    public const string ParticipantInstructions = """
        Analise da perspectiva da equipe interna: users sao usuarios internos; contacts sao contatos externos.
        Associe ownerUserId/authorUserId/userId aos ids de users quando disponiveis, nunca ao nome do contato.
        Quem pede uma acao pode ser diferente de quem deve executa-la. Nao invente identidades ou atribua falas
        por cargo/nome da caixa. Em ambiguidade, recomende esclarecer o responsavel.
        """;
    public const string CompactInstructions = """
        Algumas reunioes contem riskContext reutilizado da analise da transcricao atual, com fatos e evidencias
        literais. Os spans preservam trechos vizinhos com offsets de caracteres; lacunas nao significam ausencia
        de risco. Considere decisoes, prazos, condicoes, negacoes, contradicoes e resolucoes posteriores.
        Cite em meetingEvidenceIds os ids dos fatos usados, incluindo evidencias que reduzam o risco; ao menos
        um por reuniao compactada. Nao invente ids. contextConfidenceScore mede suficiencia do contexto.
        Se faltar evidencia, houver atribuicao ambigua ou forem necessarios outros trechos, retorne
        needsFullMeetingContext=true ou confianca abaixo de 85 para recuperar TODAS as transcricoes.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static RiskMeetingPromptContext Build(RiskAnalysisAgentInput input)
    {
        var root = JsonSerializer.SerializeToNode(input, JsonOptions)!.AsObject();
        var full = root.ToJsonString(JsonOptions);
        var ids = new List<string>();
        var prefixes = new List<string>();
        var meetings = input.MeetingAudioAnalyses?.ToArray() ?? [];
        for (var index = 0; index < meetings.Length; index++)
        {
            var meeting = meetings[index];
            if (!MeetingRiskContextPolicy.CanReuse(meeting.RiskContext, meeting.Transcript)) continue;
            var row = root["meetingAudioAnalyses"]![index]!.AsObject();
            var prefix = $"m{index + 1}:";
            var facts = new JsonArray();
            var rowIds = new List<string>();
            var factIndex = 0;
            foreach (var fact in meeting.RiskContext!.Context.Facts)
            {
                var id = $"{prefix}f{++factIndex}";
                var spans = EvidenceSpans(meeting.Transcript, fact.EvidenceExcerpt);
                if (spans.Count == 0) { facts.Clear(); break; }
                var node = JsonSerializer.SerializeToNode(fact, JsonOptions)!.AsObject();
                node["id"] = id;
                node["spans"] = JsonSerializer.SerializeToNode(spans, JsonOptions);
                facts.Add(node);
                rowIds.Add(id);
            }
            if (facts.Count == 0) continue;
            var candidate = (JsonObject)row.DeepClone();
            candidate.Remove("transcript");
            candidate.Remove("summary");
            candidate["riskContext"] = new JsonObject
            {
                ["version"] = meeting.RiskContext.Version,
                ["transcriptFingerprint"] = meeting.RiskContext.TranscriptFingerprint,
                ["confidenceScore"] = meeting.RiskContext.Context.ConfidenceScore,
                ["facts"] = facts
            };
            // Dense evidence or short transcripts cost less in their original form.
            if (candidate.ToJsonString(JsonOptions).Length >= row.ToJsonString(JsonOptions).Length) continue;
            root["meetingAudioAnalyses"]![index] = candidate;
            ids.AddRange(rowIds);
            prefixes.Add(prefix);
        }
        return new(full, root.ToJsonString(JsonOptions), ids, prefixes, prefixes.Count);
    }

    private static List<SourceSpan> EvidenceSpans(string transcript, string excerpt)
    {
        var spans = new List<SourceSpan>();
        var searchAt = 0;
        while (searchAt < transcript.Length)
        {
            var offset = transcript.IndexOf(excerpt, searchAt, StringComparison.Ordinal);
            if (offset < 0) break;
            // Preserve all occurrences rather than choosing a potentially different speaker.
            if (spans.Count >= 20) return [];
            var start = Math.Max(0, offset - 240);
            var end = Math.Min(transcript.Length, offset + excerpt.Length + 240);
            spans.Add(new(start, end, transcript[start..end]));
            searchAt = offset + excerpt.Length;
        }
        return spans;
    }

    private sealed record SourceSpan(int StartChar, int EndChar, string Text);
}
