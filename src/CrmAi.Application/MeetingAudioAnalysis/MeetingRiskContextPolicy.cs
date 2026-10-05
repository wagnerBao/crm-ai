using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAi.Domain;

namespace CrmAi.Application;

public static class MeetingRiskContextPolicy
{
    public const string Version = "meeting-risk-context-v1";
    public const string Instructions = """
        Em riskContext, registre todos os fatos comerciais relevantes para risco: decisoes, compromissos,
        prazos, objecoes, contradicoes e resolucoes, inclusive uma confirmacao posterior que invalide uma suspeita.
        Cada fato precisa de evidenceExcerpt literal da transcricao, com contexto suficiente para preservar
        negacoes e quem falou. Use kind decision, commitment, objection, contradiction ou resolution.
        Participant somente quando explicito; nunca deduza o responsavel pelo nome da caixa ou cargo.
        DueAt somente para data/hora explicita sustentada; preserve prazos relativos na descricao sem inventar a data.
        Use no maximo 40 fatos; se isso nao cobrir a reuniao inteira, marque needsFullTranscript=true.
        Nao confunda quem solicita uma acao com quem deve executa-la. Preserve valores, datas e condicoes.
        confidenceScore mede a cobertura de TODOS os fatos relevantes, nao apenas a certeza dos fatos extraidos.
        Se houver atribuicao incerta, evidencia insuficiente ou fatos relevantes que nao caibam no resumo,
        use needsFullTranscript=true. Sem fatos sustentados, devolva facts vazio e needsFullTranscript=true.
        """;

    public static string Fingerprint(string transcript) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transcript)));

    public static StoredMeetingRiskContext? Bind(MeetingRiskContext? context, string transcript) =>
        IsValid(context, transcript) ? new(Version, Fingerprint(transcript), context!) : null;

    public static bool CanReuse(StoredMeetingRiskContext? stored, string transcript) =>
        stored is not null && stored.Version == Version
        && stored.TranscriptFingerprint == Fingerprint(transcript)
        && IsValid(stored.Context, transcript);

    public static StoredMeetingRiskContext? Read(string? analysisJson)
    {
        if (string.IsNullOrWhiteSpace(analysisJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(analysisJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty("storedRiskContext", out var value)
                ? value.Deserialize<StoredMeetingRiskContext>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsValid(MeetingRiskContext? context, string transcript) =>
        context is { NeedsFullTranscript: false, ConfidenceScore: >= 85 and <= 100, Facts.Count: > 0 }
        && context.Facts.All(fact => fact is not null
            && new[] { "decision", "commitment", "objection", "contradiction", "resolution" }.Contains(fact.Kind)
            && !string.IsNullOrWhiteSpace(fact.Description)
            && !string.IsNullOrWhiteSpace(fact.EvidenceExcerpt)
            && fact.EvidenceExcerpt.Length >= 12
            && transcript.Contains(fact.EvidenceExcerpt, StringComparison.Ordinal)
            && (fact.DueAt is null || DateTimeOffset.TryParse(fact.DueAt, out _)));
}
