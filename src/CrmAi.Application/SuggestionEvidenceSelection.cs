using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrmAi.Application;

internal sealed record SelectedSuggestionEvidence(
    SuggestionCompletionVerificationInput Original,
    SuggestionCompletionVerificationInput Selected,
    IReadOnlyCollection<string> OmittedIds,
    string Reason)
{
    public bool HasSelection => OmittedIds.Count > 0;
}

public static class SuggestionEvidenceSelectionPolicy
{
    public const string Version = "suggestion-evidence-selection-v1";
    public const string Instructions = """
        evidence contem uma selecao conservadora de episodios; outros registros coletados foram omitidos.
        Stream identifica a conversa com um codigo local; nao associe falas de conversas diferentes sem evidencia.
        IDs anteriores a sugestao sao contexto, nunca prova de execucao posterior. outgoing e equipe interna;
        incoming e contato externo. Preserve quem solicita e quem executa; nao sugira responder a si mesmo.
        Compare o objeto exato da acao, responsavel, prazo, condicoes e confirmacoes ou contradicoes posteriores.
        Mensagens como 'agora foi' ou 'nao funcionou' precisam do contexto do episodio, nao de suposicoes.
        needsFullEvidence=true se faltar contexto, houver ambiguidade ou for necessario consultar outros registros.
        A ausencia de evidencia no subconjunto nao prova unfulfilled. Para fulfilled ou unfulfilled, cite ao menos
        uma evidencia posterior que sustente explicitamente a decisao. Confidence mede suficiencia do contexto.
        Nao invente IDs nem fatos dos registros omitidos. Sem decisao sustentada, use inconclusive ou needsFullEvidence=true.
        """;

    public static string ResolveMode(OpenAiRiskAnalysisOptions options, string? companyId)
    {
        var mode = options.SuggestionEvidenceSelectionMode.Trim().ToLowerInvariant();
        if (mode is not ("full" or "shadow" or "selected"))
            throw new InvalidOperationException("OpenAI:SuggestionEvidenceSelectionMode must be full, shadow or selected.");
        return mode != "full" && options.SuggestionEvidenceSelectionCompanyIds.Contains(companyId, StringComparer.OrdinalIgnoreCase)
            ? mode : "full";
    }
}

internal static class SuggestionEvidenceSelector
{
    // Keep complete episodes, not an arbitrary top-N list or isolated matching messages.
    private static readonly TimeSpan EpisodeGap = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> StopWords = Tokens("""
        a ao aos as o os de da das do dos e em no na nas nos para por com sem um uma uns umas que se do seu sua seus suas
        ele ela eles elas eu voce voces nos me mim mesmo sobre como qual quando onde este esta isso esse essa aquele
        responder resposta respostas validar validacao verificar confirmar recebimento solicitado solicitar solicitacao
        alinhar esclarecer realizar fazer executar atividade acao sugestao sugerida investigar posteriormente corretamente
        conferir acompanhar enviar retornar retorno tratar atendimento conversa contato equipe cliente interno externo
        incoming outgoing text audio image video document whatsapp instagram null true false canal tipo titulo description notes
        the a an and or to of in on with for from is are be this that it you we they please verify check confirm respond send
        """).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> StrongTopics = Tokens("""
        botao codigo rastreamento clique contrato proposta orcamento boleto pagamento campanha site pagina produto pedido
        """).ToHashSet(StringComparer.Ordinal);
    private static readonly string[] SafetyPrefixes =
    [
        "nao", "nem", "nunca", "ainda", "porem", "mas", "embora", "volt", "persist", "pend", "falt", "cancel", "desist",
        "funcion", "resolv", "conclu", "finaliz", "validei", "test", "feito", "receb", "recebi", "enviei", "enviad",
        "aprov", "recus", "err", "falh", "problema", "agora", "ok", "certo", "deu", "obrigad",
        "amanha", "hoje", "ontem", "prazo", "data", "hora", "ate", "depois", "antes", "quando", "assim", "isso",
        "promet", "combin", "comprom", "vou", "vamos", "precis", "aguard", "pode", "podem", "conseg", "dev", "urg",
        "not", "never", "still", "done", "sent", "received", "worked", "failed", "deadline", "tomorrow", "today", "will"
    ];

    public static SelectedSuggestionEvidence Select(SuggestionCompletionVerificationInput input)
    {
        if (input.Evidence.Count < 12) return Unchanged(input, "small-pool");
        if (input.Evidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != input.Evidence.Count)
            return Unchanged(input, "duplicate-evidence-ids");
        var query = input.Title + " " + input.Description + " " + PayloadText(input.Payload);
        var anchors = Tokens(query).Where(token => token.Length >= 3 && !StopWords.Contains(token)).ToHashSet(StringComparer.Ordinal);
        foreach (var actor in ActorTerms(input)) anchors.Remove(actor);
        if (anchors.Count < 2 && !anchors.Any(token => StrongTopics.Contains(token) || token.Any(char.IsDigit)))
            return Unchanged(input, "ambiguous-subject");

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.Evidence)
        {
            // No pruning of structured CRM evidence or messages whose conversation is unknown.
            if (item.BeforeSuggestion || item.OccurredAt <= input.CreatedAt || item.Type is not ("whatsapp_message" or "instagram_message")
                || string.IsNullOrWhiteSpace(item.SourceStreamId)) keep.Add(item.Id);
        }
        var streams = input.Evidence.Where(item => item.Type is "whatsapp_message" or "instagram_message"
                && !string.IsNullOrWhiteSpace(item.SourceStreamId))
            .GroupBy(item => item.Type + ":" + item.SourceStreamId, StringComparer.Ordinal);
        foreach (var stream in streams)
        {
            var messages = stream.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            var episodes = new List<List<SuggestionCompletionEvidence>>();
            foreach (var message in messages)
            {
                if (episodes.Count == 0 || message.OccurredAt - episodes[^1][^1].OccurredAt > EpisodeGap)
                    episodes.Add([]);
                episodes[^1].Add(message);
            }
            var protectedIds = new HashSet<string>(StringComparer.Ordinal) { messages[0].Id, messages[^1].Id };
            var firstPosterior = messages.FirstOrDefault(item => !item.BeforeSuggestion);
            if (firstPosterior is not null) protectedIds.Add(firstPosterior.Id);
            foreach (var direction in new[] { "incoming", "outgoing" })
            {
                var latest = messages.LastOrDefault(item => Direction(item.Summary) == direction);
                if (latest is not null) protectedIds.Add(latest.Id);
            }
            foreach (var episode in episodes)
            {
                if (episode.Any(item => keep.Contains(item.Id) || protectedIds.Contains(item.Id)
                        || MustKeep(item, anchors)))
                    foreach (var item in episode) keep.Add(item.Id);
            }
        }
        // Preserve the supplied order and every field of the surviving records.
        var selected = input.Evidence.Where(item => keep.Contains(item.Id)).ToArray();
        var omitted = input.Evidence.Where(item => !keep.Contains(item.Id)).Select(item => item.Id).ToArray();
        if (omitted.Length == 0 || !selected.Any(item => !item.BeforeSuggestion)) return Unchanged(input, "no-safe-reduction");
        return new(input, input with { Evidence = selected }, omitted, "unrelated-message-episodes");
    }

    private static bool MustKeep(SuggestionCompletionEvidence evidence, HashSet<string> anchors)
    {
        var parts = evidence.Summary.Split('|', 3);
        // Unsupported/media formats and clipped summaries cannot be safely treated as unrelated.
        if (Direction(evidence.Summary) is not ("incoming" or "outgoing") || parts.Length != 3 || parts[1].Trim() is not ("text" or "conversation" or "extendedTextMessage")
            || evidence.Summary.Length >= 1200) return true;
        var text = parts[2].Trim();
        // Speaker labels are retained in the original record, but are not topic matches.
        var colon = text.IndexOf(':');
        if (colon >= 0 && colon < 60 && !text[..colon].Contains('/') && text[..colon].Split(' ').Length <= 4)
            text = text[(colon + 1)..];
        var tokens = Tokens(text).ToArray();
        if (tokens.Length < 8 || tokens.Any(token => token.Any(char.IsDigit))) return true;
        if (tokens.Any(token => SafetyPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))) return true;
        if (tokens.Any(token => anchors.Contains(token))) return true;
        // Links, amounts and questions often carry a task or a deadline even without lexical overlap.
        return text.Contains('?') || text.Contains('@') || text.Contains("http", StringComparison.OrdinalIgnoreCase)
            || text.Contains("R$", StringComparison.OrdinalIgnoreCase);
    }

    private static string Direction(string summary) => summary.Split('|', 2)[0].Trim().ToLowerInvariant();

    private static IEnumerable<string> ActorTerms(SuggestionCompletionVerificationInput input)
    {
        if (input.Payload.ValueKind == JsonValueKind.Object)
            foreach (var field in new[] { "contactName", "ownerUserName", "mailboxName" })
                if (input.Payload.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    foreach (var token in Tokens(value.GetString() ?? "")) yield return token;
        foreach (var item in input.Evidence.Where(item => item.Type is "whatsapp_message" or "instagram_message"))
        {
            var parts = item.Summary.Split('|', 3);
            if (parts.Length != 3) continue;
            var text = parts[2].Trim();
            var colon = text.IndexOf(':');
            if (colon >= 0 && colon < 60 && !text[..colon].Contains('/') && text[..colon].Split(' ').Length <= 4)
                foreach (var token in Tokens(text[..colon])) yield return token;
        }
    }

    private static string PayloadText(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "";
        return string.Join(" ", new[] { "title", "description", "notes", "subject", "activityTitle", "activityNotes" }
            .Select(field => payload.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null));
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var normalized = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                normalized.Append(char.ToLowerInvariant(character));
        foreach (Match match in Regex.Matches(normalized.ToString(), "[a-z0-9]+", RegexOptions.CultureInvariant))
            yield return match.Value switch
            {
                "botoes" => "botao", "codigos" => "codigo", "paginas" => "pagina", "contratos" => "contrato",
                "propostas" => "proposta", "pedidos" => "pedido", "campanhas" => "campanha", _ => match.Value
            };
    }

    private static SelectedSuggestionEvidence Unchanged(SuggestionCompletionVerificationInput input, string reason) =>
        new(input, input, [], reason);
}
