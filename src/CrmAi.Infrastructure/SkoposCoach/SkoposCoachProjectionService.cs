using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrmAi.Application;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.SkoposCoach;

public sealed partial class SkoposCoachProjectionService(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    SkoposCoachSynthesisClient synthesisClient,
    ILogger<SkoposCoachProjectionService> logger)
{
    private const string AgentKey = "skopos-coach";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TopicRule[] Rules =
    [
        new("follow_up", "Cadência e próximos passos", "Reforçar cadência, registro do próximo passo e retorno no prazo combinado.", ["follow-up", "follow up", "retorno", "sem contato", "proximo passo", "pendente", "reativ"]),
        new("qualification", "Qualificação e descoberta", "Aprofundar necessidade, urgência, autoridade e critérios de decisão antes de avançar.", ["qualifica", "decisor", "necessidade", "diagnost", "dor", "objec"]),
        new("proposal", "Proposta e negociação", "Treinar apresentação de valor, tratamento de preço e combinados posteriores à proposta.", ["proposta", "orcamento", "preco", "pagamento", "condicao", "negocia"]),
        new("risk", "Prevenção de risco comercial", "Atuar cedo sobre deterioração, atrasos e sinais consistentes de perda.", ["risco", "perda", "atras", "deteriora", "inativ", "estagn"]),
        new("product", "Domínio de produto", "Conectar benefícios e casos de uso dos produtos com o contexto comercial observado.", ["produto", "servico", "solucao", "beneficio", "entrega"]),
        new("productivity", "Execução e produtividade", "Melhorar consistência de atividades, metas e conclusão dos compromissos comerciais.", ["atividade", "meta", "efetividade", "produtiv", "checkout", "realizado"])
    ];
    private static readonly SourceProjection[] Projections =
    [
        new("whatsapp", "whatsapp-conversation-analysis", ["whatsapp-conversation-analysis"], SkoposCoachProjectionSql.Whatsapp),
        new("meeting", "meeting-service-analysis", ["meeting-service-analysis", "call-audio-analysis"], SkoposCoachProjectionSql.Meeting),
        new("opportunity_risk", "risk-analysis", ["risk-analysis"], SkoposCoachProjectionSql.Risk),
        new("daily_checkout", "daily-checkout", ["daily-checkout"], SkoposCoachProjectionSql.Checkout)
    ];

    public async Task ProjectAndProcessAsync(CancellationToken cancellationToken)
    {
        await ProjectSourcesAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await QueueDailyRunsAsync(connection, cancellationToken);
        while (await ClaimRunAsync(connection, cancellationToken) is { } run)
        {
            try { await ProcessRunAsync(connection, run, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Skopos Coach run {RunId} failed.", run.Id);
                await FailRunAsync(connection, run.Id, exception.GetType().Name, cancellationToken);
            }
        }
    }

    private async Task ProjectSourcesAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(Projections.Select(projection => ProjectSourceAsync(projection, cancellationToken)));

    private async Task ProjectSourceAsync(SourceProjection projection, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(projection.Sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await UpdateSourceHealthAsync(connection, projection, null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Skopos Coach source projection {SourceType} failed without interrupting the other sources.", projection.SourceType);
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await UpdateSourceHealthAsync(connection, projection, $"Falha de projeção ({exception.GetType().Name}).", cancellationToken);
            }
            catch (Exception healthException) when (healthException is not OperationCanceledException)
            {
                logger.LogWarning(healthException, "Could not persist Skopos Coach source health for {SourceType}.", projection.SourceType);
            }
        }
    }

    private static async Task UpdateSourceHealthAsync(NpgsqlConnection connection, SourceProjection projection, string? error, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.UpdateHealth, connection);
        command.Parameters.AddWithValue("sourceType", projection.SourceType);
        command.Parameters.AddWithValue("sourceAgentKey", projection.SourceAgentKey);
        command.Parameters.AddWithValue("agentKeys", projection.AgentKeys);
        command.Parameters.Add("error", NpgsqlDbType.Text).Value = error is null ? DBNull.Value : error;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task QueueDailyRunsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.QueueDailyRuns, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RunRow?> ClaimRunAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.ClaimRun, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateOnly>(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4));
    }

    private async Task ProcessRunAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(AgentKey, run.CompanyId.ToString(), cancellationToken);
        if (!settings.IsActive) throw new InvalidOperationException("Skopos Coach is inactive for this company.");

        var reports = Deduplicate(await ReadReportsAsync(connection, run, cancellationToken));
        var context = await ReadCommercialContextAsync(connection, run, cancellationToken);
        var coverage = await ReadCoverageAsync(connection, run, cancellationToken);
        var candidates = reports.Where(report => report.GroupId.HasValue)
            .GroupBy(report => report.GroupId!.Value)
            .SelectMany(group => Rules.Select(rule => new Candidate(group.Key, rule, group.Where(report => Matches(report, rule)).ToArray())))
            .Where(candidate => candidate.Reports.Length > 0)
            .ToArray();

        CoachSynthesisResult? synthesis = null;
        string? synthesisError = null;
        try
        {
            synthesis = await synthesisClient.AnalyzeAsync(settings, new
            {
                period = new { from = run.From, to = run.To },
                candidates = candidates.Select(candidate => new
                {
                    gapKey = candidate.Rule.Category,
                    candidate.Rule.Category,
                    groupId = candidate.GroupId,
                    evidenceCount = candidate.Reports.Length,
                    collaboratorCount = candidate.Reports.Select(report => report.OwnerUserId).Where(id => id.HasValue).Distinct().Count(),
                    evidence = candidate.Reports.Take(20).Select(report => new
                    {
                        id = report.Id,
                        source = report.SourceType,
                        summary = Truncate(Sanitize(report.Summary), 600)
                    })
                }),
                coverage,
                commercialContext = JsonSerializer.Deserialize<object>(context, JsonOptions)
            }, run.CompanyId.ToString(), cancellationToken);
            if (synthesis is null) synthesisError = "Síntese indisponível por configuração incompleta.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            synthesisError = $"Síntese indisponível ({exception.GetType().Name}); consolidação determinística preservada.";
            logger.LogWarning(exception, "Skopos Coach synthesis failed; deterministic consolidation will be preserved for run {RunId}.", run.Id);
        }

        bool Confirmed(Candidate candidate)
        {
            var modelItem = FindValidModelItem(candidate, synthesis);
            return IsConfirmed(candidate) && (modelItem?.Confidence ?? Confidence(candidate)) >= 70;
        }

        var trends = candidates.Where(candidate => !Confirmed(candidate)).Select(candidate => new
        {
            gapKey = candidate.Rule.Category,
            category = candidate.Rule.Category,
            groupId = candidate.GroupId,
            title = candidate.Rule.Title,
            evidenceCount = candidate.Reports.Length,
            collaboratorCount = candidate.Reports.Select(x => x.OwnerUserId).Where(x => x.HasValue).Distinct().Count(),
            confidence = Confidence(candidate),
            reason = TrendReason(candidate)
        }).ToArray();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var candidate in candidates.Where(Confirmed))
            await UpsertTopicAsync(connection, transaction, run, candidate, FindValidModelItem(candidate, synthesis), cancellationToken);

        var unavailableSources = coverage.Count(item => item.Status is "disabled" or "unconfigured" or "error");
        var status = unavailableSources > 0 || synthesisError is not null ? "partial" : "completed";
        var executiveSummary = !string.IsNullOrWhiteSpace(synthesis?.ExecutiveSummary)
            ? Truncate(Sanitize(synthesis.ExecutiveSummary), 600)
            : reports.Count == 0
                ? "Ainda não há relatórios analíticos suficientes no período selecionado."
                : $"Foram consolidados {reports.Count} relatórios analíticos. Os temas confirmados respeitam o mínimo de evidências, colaboradores e confiança.";
        await using var update = new NpgsqlCommand(SkoposCoachProjectionSql.CompleteRun, connection, transaction);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("reportCount", reports.Count);
        update.Parameters.AddWithValue("evidenceCount", reports.Sum(report => report.RepresentedEvidenceCount));
        update.Parameters.AddWithValue("summary", executiveSummary);
        update.Parameters.AddWithValue("coverage", JsonSerializer.Serialize(coverage, JsonOptions));
        update.Parameters.AddWithValue("context", context);
        update.Parameters.AddWithValue("trends", JsonSerializer.Serialize(trends, JsonOptions));
        update.Parameters.AddWithValue("model", settings.Model);
        update.Parameters.AddWithValue("fingerprint", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Instructions))));
        update.Parameters.Add("error", NpgsqlDbType.Text).Value = synthesisError is null ? DBNull.Value : synthesisError;
        update.Parameters.AddWithValue("id", run.Id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static List<ReportRow> Deduplicate(IEnumerable<ReportRow> reports) => reports
        .GroupBy(report => new { report.SourceType, EntityId = report.OpportunityId ?? report.SourceId, report.GroupId })
        .Select(group => group.OrderByDescending(item => item.OccurredAt).First())
        .ToList();

    private static async Task<List<ReportRow>> ReadReportsAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.ReadReports, connection);
        AddRunParameters(command, run);
        var result = new List<ReportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetDateTime(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9)));
        return result;
    }

    private static async Task<CoverageRow[]> ReadCoverageAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.ReadCoverage, connection);
        AddRunParameters(command, run);
        var result = new List<CoverageRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3),
                reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return result.ToArray();
    }

    private static async Task<string> ReadCommercialContextAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.ReadCommercialContext, connection);
        AddRunParameters(command, run);
        return (string)(await command.ExecuteScalarAsync(cancellationToken) ?? "{}");
    }

    private static void AddRunParameters(NpgsqlCommand command, RunRow run)
    {
        command.Parameters.AddWithValue("companyId", run.CompanyId);
        command.Parameters.AddWithValue("from", run.From);
        command.Parameters.AddWithValue("to", run.To);
        command.Parameters.Add("groups", NpgsqlDbType.Jsonb).Value = run.GroupIds;
    }

    private static async Task UpsertTopicAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, RunRow run, Candidate candidate, CoachSynthesisItem? synthesis, CancellationToken cancellationToken)
    {
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@company), hashtext(@gap))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("company", run.CompanyId.ToString());
            lockCommand.Parameters.AddWithValue("gap", $"{candidate.GroupId}:{candidate.Rule.Category}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid? existingId = null;
        string? existingStatus = null;
        await using (var find = new NpgsqlCommand(SkoposCoachProjectionSql.FindTopic, connection, transaction))
        {
            find.Parameters.AddWithValue("companyId", run.CompanyId);
            find.Parameters.AddWithValue("groupId", candidate.GroupId);
            find.Parameters.AddWithValue("gapKey", candidate.Rule.Category);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingId = reader.GetGuid(0);
                existingStatus = reader.GetString(1);
            }
        }

        if (existingStatus == "archived") return;
        var reports = SelectEvidence(candidate, synthesis);
        var confidence = synthesis is null ? Confidence(candidate) : Math.Min(Confidence(candidate), Math.Clamp(synthesis.Confidence, 0, 100));
        Guid topicId;
        if (existingId.HasValue && existingStatus is "draft" or "approved" or "in_progress")
        {
            topicId = existingId.Value;
            await using var update = new NpgsqlCommand(SkoposCoachProjectionSql.UpdateRecurringTopic, connection, transaction);
            update.Parameters.AddWithValue("runId", run.Id);
            update.Parameters.AddWithValue("confidence", confidence);
            update.Parameters.AddWithValue("impact", Math.Min(100, 50 + reports.Length * 5));
            update.Parameters.AddWithValue("objective", Truncate(synthesis?.Objective?.Trim() ?? candidate.Rule.Action, 600));
            update.Parameters.AddWithValue("audience", Truncate(synthesis?.TargetAudience?.Trim() ?? "Equipe comercial", 160));
            update.Parameters.AddWithValue("format", Truncate(synthesis?.Format?.Trim() ?? "Workshop prático", 120));
            update.Parameters.AddWithValue("duration", Math.Clamp(synthesis?.DurationMinutes ?? 45, 5, 480));
            update.Parameters.AddWithValue("outline", JsonSerializer.Serialize((synthesis?.Outline ?? [candidate.Rule.Action]).Take(8).Select(item => Truncate(Sanitize(item), 300)), JsonOptions));
            update.Parameters.AddWithValue("action", Truncate(synthesis?.RecommendedAction?.Trim() ?? candidate.Rule.Action, 600));
            update.Parameters.AddWithValue("id", topicId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            topicId = Guid.NewGuid();
            await using var insert = new NpgsqlCommand(SkoposCoachProjectionSql.InsertTopic, connection, transaction);
            insert.Parameters.AddWithValue("id", topicId);
            insert.Parameters.AddWithValue("companyId", run.CompanyId);
            insert.Parameters.AddWithValue("runId", run.Id);
            insert.Parameters.Add("previous", NpgsqlDbType.Uuid).Value = existingStatus == "completed" && existingId.HasValue ? existingId.Value : DBNull.Value;
            insert.Parameters.AddWithValue("groupId", candidate.GroupId);
            insert.Parameters.AddWithValue("gapKey", candidate.Rule.Category);
            insert.Parameters.AddWithValue("title", Truncate(synthesis?.Title?.Trim() ?? candidate.Rule.Title, 120));
            insert.Parameters.AddWithValue("summary", Truncate(synthesis?.Justification?.Trim() ?? candidate.Rule.Action, 800));
            insert.Parameters.AddWithValue("category", candidate.Rule.Category);
            insert.Parameters.AddWithValue("objective", Truncate(synthesis?.Objective?.Trim() ?? candidate.Rule.Action, 600));
            insert.Parameters.AddWithValue("audience", Truncate(synthesis?.TargetAudience?.Trim() ?? "Equipe comercial", 160));
            insert.Parameters.AddWithValue("priority", NormalizePriority(synthesis?.Priority));
            insert.Parameters.AddWithValue("format", Truncate(synthesis?.Format?.Trim() ?? "Workshop prático", 120));
            insert.Parameters.AddWithValue("duration", Math.Clamp(synthesis?.DurationMinutes ?? 45, 5, 480));
            insert.Parameters.AddWithValue("outline", JsonSerializer.Serialize((synthesis?.Outline ?? [candidate.Rule.Action]).Take(8).Select(item => Truncate(Sanitize(item), 300)), JsonOptions));
            insert.Parameters.AddWithValue("confidence", confidence);
            insert.Parameters.AddWithValue("evidenceCount", reports.Length);
            insert.Parameters.AddWithValue("collaborators", reports.Select(report => report.OwnerUserId).Where(id => id.HasValue).Distinct().Count());
            insert.Parameters.AddWithValue("impact", Math.Min(100, 50 + reports.Length * 5));
            insert.Parameters.AddWithValue("action", Truncate(synthesis?.RecommendedAction?.Trim() ?? candidate.Rule.Action, 600));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var report in reports)
        {
            await using var evidence = new NpgsqlCommand(SkoposCoachProjectionSql.InsertEvidence, connection, transaction);
            evidence.Parameters.AddWithValue("topicId", topicId);
            evidence.Parameters.AddWithValue("reportId", report.Id);
            evidence.Parameters.AddWithValue("relevance", confidence);
            evidence.Parameters.AddWithValue("excerpt", Truncate(Sanitize(report.Summary), 280));
            await evidence.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var recount = new NpgsqlCommand(SkoposCoachProjectionSql.RecountTopic, connection, transaction);
        recount.Parameters.AddWithValue("topicId", topicId);
        await recount.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CoachSynthesisItem? FindValidModelItem(Candidate candidate, CoachSynthesisResult? synthesis)
    {
        var item = synthesis?.Items.FirstOrDefault(value =>
            string.Equals(value.Category, candidate.Rule.Category, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.GapKey, candidate.Rule.Category, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(value.GroupId, out var groupId) && groupId == candidate.GroupId);
        if (item is null || item.Confidence is < 0 or > 100) return null;
        var known = candidate.Reports.Select(report => report.Id).ToHashSet();
        var evidence = item.EvidenceIds.Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty).ToArray();
        if (evidence.Length < 5 || evidence.Any(id => !known.Contains(id))) return null;
        var collaborators = candidate.Reports.Where(report => evidence.Contains(report.Id)).Select(report => report.OwnerUserId).Where(id => id.HasValue).Distinct().Count();
        return collaborators >= 2 ? item : null;
    }

    private static ReportRow[] SelectEvidence(Candidate candidate, CoachSynthesisItem? synthesis)
    {
        if (synthesis is null) return candidate.Reports;
        var ids = synthesis.EvidenceIds.Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty).ToHashSet();
        return candidate.Reports.Where(report => ids.Contains(report.Id)).ToArray();
    }

    private static bool Matches(ReportRow report, TopicRule rule)
    {
        var text = RemoveDiacritics($"{report.Summary} {report.InsightsJson}").ToLowerInvariant();
        return rule.Keywords.Any(text.Contains);
    }
    private static bool IsConfirmed(Candidate candidate) => MeetsConfirmationThreshold(candidate.Reports.Length, candidate.Reports.Select(x => x.OwnerUserId).Where(x => x.HasValue).Distinct().Count(), Confidence(candidate));
    internal static bool MeetsConfirmationThreshold(int evidenceCount, int collaboratorCount, int confidence) => evidenceCount >= 5 && collaboratorCount >= 2 && confidence >= 70;
    private static int Confidence(Candidate candidate) => Math.Min(95, 60 + candidate.Reports.Length * 3 + candidate.Reports.Select(x => x.SourceType).Distinct().Count() * 3);
    private static string TrendReason(Candidate candidate) => candidate.Reports.Length < 5 ? "Menos de 5 evidências." : candidate.Reports.Select(x => x.OwnerUserId).Where(x => x.HasValue).Distinct().Count() < 2 ? "Menos de 2 colaboradores." : "Confiança abaixo de 70%.";
    private static string NormalizePriority(string? value) => value?.Trim().ToLowerInvariant() is "low" or "medium" or "high" or "critical" ? value.Trim().ToLowerInvariant() : "medium";
    private static string Sanitize(string text) => SanitizeForCoach(text);
    internal static string SanitizeForCoach(string text) => PhoneRegex().Replace(EmailRegex().Replace(text, "[email]"), "[telefone]").Trim();
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
    private static string RemoveDiacritics(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark));
    private static async Task FailRunAsync(NpgsqlConnection connection, Guid id, string errorType, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SkoposCoachProjectionSql.FailRun, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("error", $"Falha ao processar a análise ({errorType}). Tente novamente.");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)] private static partial Regex EmailRegex();
    [GeneratedRegex(@"(?<!\d)(?:\+?55\s*)?(?:\(?\d{2}\)?\s*)?\d{4,5}[-\s]?\d{4}(?!\d)")] private static partial Regex PhoneRegex();

    private sealed record SourceProjection(string SourceType, string SourceAgentKey, string[] AgentKeys, string Sql);
    private sealed record RunRow(Guid Id, Guid CompanyId, DateOnly From, DateOnly To, string GroupIds);
    private sealed record ReportRow(Guid Id, string SourceType, Guid SourceId, Guid? GroupId, Guid? OwnerUserId, Guid? OpportunityId, DateTime OccurredAt, string Summary, string InsightsJson, int RepresentedEvidenceCount);
    private sealed record CoverageRow(string SourceType, string Status, bool IsConfigured, bool IsActive, int Count, DateTime? LastOccurredAt, DateTime? LastProjectedAt, string? ErrorMessage);
    private sealed record TopicRule(string Category, string Title, string Action, string[] Keywords);
    private sealed record Candidate(Guid GroupId, TopicRule Rule, ReportRow[] Reports);
}
