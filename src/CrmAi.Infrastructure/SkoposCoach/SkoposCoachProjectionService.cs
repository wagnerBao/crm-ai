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
        new("follow_up", "Cadencia e proximos passos", "Reforcar cadencia, registro do proximo passo e retorno no prazo combinado.", ["follow-up", "follow up", "retorno", "sem contato", "proximo passo", "pendente", "reativ"]),
        new("qualification", "Qualificacao e descoberta", "Aprofundar necessidade, urgencia, autoridade e criterios de decisao antes de avancar.", ["qualifica", "decisor", "necessidade", "diagnost", "dor", "objec"]),
        new("proposal", "Proposta e negociacao", "Treinar apresentacao de valor, tratamento de preco e combinados posteriores a proposta.", ["proposta", "orcamento", "preco", "pagamento", "condicao", "negocia"]),
        new("risk", "Prevencao de risco comercial", "Atuar cedo sobre deterioracao, atrasos e sinais consistentes de perda.", ["risco", "perda", "atras", "deteriora", "inativ", "estagn"]),
        new("product", "Dominio de produto", "Conectar beneficios e casos de uso dos produtos com o contexto comercial observado.", ["produto", "servico", "solucao", "beneficio", "entrega"]),
        new("productivity", "Execucao e produtividade", "Melhorar consistencia de atividades, metas e conclusao dos compromissos comerciais.", ["atividade", "meta", "efetividade", "produtiv", "checkout", "realizado"])
    ];

    public async Task ProjectAndProcessAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ProjectSourcesAsync(connection, cancellationToken);
        await QueueDailyRunsAsync(connection, cancellationToken);
        while (await ClaimRunAsync(connection, cancellationToken) is { } run)
        {
            try { await ProcessRunAsync(connection, run, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Skopos Coach run {RunId} failed.", run.Id);
                await FailRunAsync(connection, run.Id, exception.Message, cancellationToken);
            }
        }
    }

    private static async Task ProjectSourcesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO skopos_coach_agent_reports
                (company_id, source_type, source_id, group_id, owner_user_id, opportunity_id, occurred_at,
                 report_summary, insights_json, source_version, updated_at)
            SELECT run.company_id, 'whatsapp', run.id, owner.group_id, coalesce(opportunity.owner_user_id, conversation.owner_user_id),
                   opportunity.id, run.window_end_at, left(run.summary, 4000),
                   jsonb_build_object('confidence', round(coalesce(insight.confidence, 0) * 100), 'analysisKind', insight.kind),
                   run.updated_at, now()
            FROM whatsapp_conversation_analysis_runs run
            JOIN whatsapp_conversations conversation ON conversation.id = run.conversation_id
            LEFT JOIN LATERAL (
                SELECT o.* FROM opportunity_contacts oc JOIN opportunities o ON o.id = oc.opportunity_id
                WHERE oc.contact_id = conversation.contact_id AND o.company_id = run.company_id
                ORDER BY o.updated_at DESC LIMIT 1
            ) opportunity ON true
            LEFT JOIN users owner ON owner.id = coalesce(opportunity.owner_user_id, conversation.owner_user_id)
            LEFT JOIN ai_insights insight ON insight.id = run.ai_insight_id
            WHERE run.status = 'completed' AND nullif(btrim(run.summary), '') IS NOT NULL
              AND run.window_end_at >= now() - interval '30 days' AND run.company_id IS NOT NULL
            ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
                group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
                opportunity_id = excluded.opportunity_id, occurred_at = excluded.occurred_at,
                report_summary = excluded.report_summary, insights_json = excluded.insights_json,
                source_version = excluded.source_version, updated_at = now()
            WHERE skopos_coach_agent_reports.source_version < excluded.source_version;

            INSERT INTO skopos_coach_agent_reports
                (company_id, source_type, source_id, group_id, owner_user_id, opportunity_id, occurred_at,
                 report_summary, insights_json, source_version, updated_at)
            SELECT recording.company_id, 'meeting', recording.id, owner.group_id,
                   coalesce(activity.owner_user_id, opportunity.owner_user_id), recording.opportunity_id,
                   recording.ended_at, left(recording.summary, 4000),
                   jsonb_build_object('status', recording.status, 'durationMinutes', round(recording.duration_ms / 60000.0, 1)),
                   recording.updated_at, now()
            FROM meeting_audio_recordings recording
            LEFT JOIN activities activity ON activity.id = recording.activity_id
            LEFT JOIN opportunities opportunity ON opportunity.id = recording.opportunity_id
            LEFT JOIN users owner ON owner.id = coalesce(activity.owner_user_id, opportunity.owner_user_id)
            WHERE nullif(btrim(recording.summary), '') IS NOT NULL
              AND recording.ended_at >= now() - interval '30 days' AND recording.company_id IS NOT NULL
            ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
                group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
                opportunity_id = excluded.opportunity_id, occurred_at = excluded.occurred_at,
                report_summary = excluded.report_summary, insights_json = excluded.insights_json,
                source_version = excluded.source_version, updated_at = now()
            WHERE skopos_coach_agent_reports.source_version < excluded.source_version;

            INSERT INTO skopos_coach_agent_reports
                (company_id, source_type, source_id, group_id, owner_user_id, opportunity_id, occurred_at,
                 report_summary, insights_json, source_version, updated_at)
            SELECT insight.company_id, 'opportunity_risk', insight.id, owner.group_id, opportunity.owner_user_id,
                   insight.opportunity_id, insight.created_at, left(insight.message, 4000),
                   jsonb_build_object('confidence', round(coalesce(insight.confidence, 0) * 100), 'status', insight.status),
                   insight.updated_at, now()
            FROM ai_insights insight
            JOIN opportunities opportunity ON opportunity.id = insight.opportunity_id
            LEFT JOIN users owner ON owner.id = opportunity.owner_user_id
            WHERE insight.kind IN ('risk', 'risk-analysis') AND insight.created_at >= now() - interval '30 days'
              AND insight.company_id IS NOT NULL
            ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
                group_id = excluded.group_id, owner_user_id = excluded.owner_user_id,
                opportunity_id = excluded.opportunity_id, occurred_at = excluded.occurred_at,
                report_summary = excluded.report_summary, insights_json = excluded.insights_json,
                source_version = excluded.source_version, updated_at = now()
            WHERE skopos_coach_agent_reports.source_version < excluded.source_version;

            INSERT INTO skopos_coach_agent_reports
                (company_id, source_type, source_id, occurred_at, report_summary, insights_json, source_version, updated_at)
            SELECT snapshot.company_id, 'daily_checkout', snapshot.id, snapshot.snapshot_at,
                   left(concat_ws(' ', snapshot.payload_json #>> '{executiveSummary,headline}', snapshot.payload_json #>> '{executiveSummary,focus}'), 4000),
                   jsonb_build_object('totals', coalesce(snapshot.payload_json->'totals', '{}'::jsonb),
                                      'metrics', coalesce(snapshot.payload_json->'metrics', '[]'::jsonb)),
                   snapshot.updated_at, now()
            FROM daily_checkout_snapshots snapshot
            WHERE snapshot.snapshot_at >= now() - interval '30 days' AND snapshot.company_id IS NOT NULL
            ON CONFLICT (company_id, source_type, source_id) DO UPDATE SET
                occurred_at = excluded.occurred_at, report_summary = excluded.report_summary,
                insights_json = excluded.insights_json, source_version = excluded.source_version, updated_at = now()
            WHERE skopos_coach_agent_reports.source_version < excluded.source_version;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task QueueDailyRunsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO skopos_coach_runs (company_id, trigger_type, status, date_from, date_to)
            SELECT DISTINCT report.company_id, 'daily_checkout', 'pending', current_date - 29, current_date
            FROM skopos_coach_agent_reports report
            JOIN LATERAL (
                SELECT is_active FROM ai_agent_settings settings
                WHERE settings.agent_key = 'skopos-coach'
                  AND (settings.company_id = report.company_id OR settings.company_id IS NULL)
                ORDER BY settings.company_id NULLS LAST LIMIT 1
            ) settings ON settings.is_active
            WHERE report.source_type = 'daily_checkout' AND report.occurred_at::date = current_date
              AND NOT EXISTS (SELECT 1 FROM skopos_coach_runs run WHERE run.company_id = report.company_id
                              AND run.date_to = current_date AND run.trigger_type = 'daily_checkout')
              AND NOT EXISTS (SELECT 1 FROM skopos_coach_runs active WHERE active.company_id = report.company_id
                              AND active.status IN ('pending','processing'))
            ON CONFLICT DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<RunRow?> ClaimRunAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE skopos_coach_runs run SET status = 'processing', started_at = now(), updated_at = now(), attempt_count = attempt_count + 1
            WHERE run.id = (SELECT pending.id FROM skopos_coach_runs pending WHERE pending.status = 'pending'
                            ORDER BY pending.created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING run.id, run.company_id, run.date_from, run.date_to, run.group_ids::text
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateOnly>(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4));
    }

    private async Task ProcessRunAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(AgentKey, run.CompanyId.ToString(), cancellationToken);
        if (!settings.IsActive) throw new InvalidOperationException("Skopos Coach is inactive for this company.");
        var reports = await ReadReportsAsync(connection, run, cancellationToken);
        var context = await ReadCommercialContextAsync(connection, run, cancellationToken);
        var coverage = reports.GroupBy(x => x.SourceType).ToDictionary(x => x.Key, x => x.Count());
        var candidates = Rules.Select(rule => new Candidate(rule, reports.Where(report => Matches(report, rule)).ToArray()))
            .Where(candidate => candidate.Reports.Length > 0).ToArray();
        CoachSynthesisResult? synthesis = null;
        try
        {
            synthesis = await synthesisClient.AnalyzeAsync(settings, new
            {
                period = new { from = run.From, to = run.To },
                categories = candidates.Select(candidate => new
                {
                    candidate.Rule.Category,
                    evidenceCount = candidate.Reports.Length,
                    collaboratorCount = candidate.Reports.Select(report => report.OwnerUserId).Where(id => id.HasValue).Distinct().Count(),
                    sourceCount = candidate.Reports.Select(report => report.SourceType).Distinct().Count(),
                    samples = candidate.Reports.Take(8).Select(report => Sanitize(report.Summary)[..Math.Min(600, Sanitize(report.Summary).Length)])
                }),
                commercialContext = JsonSerializer.Deserialize<object>(context, JsonOptions)
            }, run.CompanyId.ToString(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Skopos Coach synthesis failed; deterministic consolidation will be preserved for run {RunId}.", run.Id);
        }
        bool Confirmed(Candidate candidate)
        {
            var modelItem = synthesis?.Items.FirstOrDefault(item => item.Category == candidate.Rule.Category);
            return IsConfirmed(candidate) && (modelItem?.Confidence ?? 100) >= 70;
        }
        var trends = candidates.Where(candidate => !Confirmed(candidate)).Select(candidate => new
        {
            category = candidate.Rule.Category,
            title = candidate.Rule.Title,
            evidenceCount = candidate.Reports.Length,
            collaboratorCount = candidate.Reports.Select(x => x.OwnerUserId).Where(x => x.HasValue).Distinct().Count(),
            confidence = Confidence(candidate),
            status = "trend"
        }).ToArray();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var candidate in candidates.Where(Confirmed))
            await InsertTopicAsync(connection, transaction, run, candidate, synthesis?.Items.FirstOrDefault(item => item.Category == candidate.Rule.Category), cancellationToken);
        var executiveSummary = !string.IsNullOrWhiteSpace(synthesis?.ExecutiveSummary) ? synthesis.ExecutiveSummary.Trim() : reports.Count == 0
            ? "Ainda nao ha relatorios analiticos suficientes no periodo selecionado."
            : $"Foram consolidados {reports.Count} relatorios analiticos em {coverage.Count} fontes. Os temas confirmados respeitam o minimo de evidencias, colaboradores e confianca.";
        const string update = """
            UPDATE skopos_coach_runs SET status = 'completed', report_count = @reportCount,
                executive_summary = @summary, source_coverage_json = @coverage::jsonb,
                commercial_context_json = @context::jsonb, trends_json = @trends::jsonb,
                model = @model, prompt_fingerprint = @fingerprint, completed_at = now(), updated_at = now(), error_message = null
            WHERE id = @id
            """;
        await using var command = new NpgsqlCommand(update, connection, transaction);
        command.Parameters.AddWithValue("reportCount", reports.Count);
        command.Parameters.AddWithValue("summary", executiveSummary);
        command.Parameters.AddWithValue("coverage", JsonSerializer.Serialize(coverage, JsonOptions));
        command.Parameters.AddWithValue("context", context);
        command.Parameters.AddWithValue("trends", JsonSerializer.Serialize(trends, JsonOptions));
        command.Parameters.AddWithValue("model", settings.Model);
        command.Parameters.AddWithValue("fingerprint", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Instructions))));
        command.Parameters.AddWithValue("id", run.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<List<ReportRow>> ReadReportsAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source_type, owner_user_id, report_summary, insights_json::text
            FROM skopos_coach_agent_reports
            WHERE company_id = @companyId AND occurred_at >= @from::date AND occurred_at < (@to::date + interval '1 day')
              AND (@groups = '[]'::jsonb OR @groups ? group_id::text)
            ORDER BY occurred_at DESC LIMIT 1000
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", run.CompanyId);
        command.Parameters.AddWithValue("from", run.From);
        command.Parameters.AddWithValue("to", run.To);
        command.Parameters.Add("groups", NpgsqlDbType.Jsonb).Value = run.GroupIds;
        var result = new List<ReportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    private static async Task<string> ReadCommercialContextAsync(NpgsqlConnection connection, RunRow run, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT jsonb_build_object(
              'products', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
                SELECT product.name, count(*)::int opportunity_count
                FROM opportunity_products link JOIN products product ON product.id = link.product_id
                JOIN opportunities opportunity ON opportunity.id = link.opportunity_id
                LEFT JOIN users owner ON owner.id = opportunity.owner_user_id
                WHERE opportunity.company_id = @companyId AND opportunity.updated_at >= @from::date
                  AND opportunity.updated_at < (@to::date + interval '1 day')
                  AND (@groups = '[]'::jsonb OR @groups ? owner.group_id::text)
                GROUP BY product.name ORDER BY count(*) DESC LIMIT 10) item),
              'ads', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
                SELECT source_platform, coalesce(campaign_name, utm_campaign, 'Sem campanha') campaign, count(*)::int leads
                FROM commercial_attribution_events WHERE company_id = @companyId AND captured_at >= @from::date
                  AND captured_at < (@to::date + interval '1 day')
                GROUP BY source_platform, coalesce(campaign_name, utm_campaign, 'Sem campanha') ORDER BY count(*) DESC LIMIT 10) item),
              'metrics', (SELECT jsonb_build_object(
                'activities', count(*)::int,
                'completedActivities', count(*) FILTER (WHERE status = 'done')::int,
                'collaborators', count(DISTINCT owner_user_id)::int)
                FROM activities WHERE company_id = @companyId AND date_at >= @from::date AND date_at < (@to::date + interval '1 day')))
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", run.CompanyId);
        command.Parameters.AddWithValue("from", run.From);
        command.Parameters.AddWithValue("to", run.To);
        command.Parameters.Add("groups", NpgsqlDbType.Jsonb).Value = run.GroupIds;
        return (string)(await command.ExecuteScalarAsync(cancellationToken) ?? "{}");
    }

    private static async Task InsertTopicAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, RunRow run, Candidate candidate, CoachSynthesisItem? synthesis, CancellationToken cancellationToken)
    {
        var topicId = Guid.NewGuid();
        var collaborators = candidate.Reports.Select(x => x.OwnerUserId).Where(x => x.HasValue).Distinct().Count();
        var confidence = synthesis is null ? Confidence(candidate) : Math.Min(Confidence(candidate), Math.Clamp(synthesis.Confidence, 0, 100));
        const string topicSql = """
            INSERT INTO skopos_coach_training_topics
                (id, company_id, run_id, title, summary, category, confidence, evidence_count, collaborator_count, impact_score, recommended_action)
            VALUES (@id, @companyId, @runId, @title, @summary, @category, @confidence, @evidenceCount, @collaborators, @impact, @action)
            """;
        await using (var command = new NpgsqlCommand(topicSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", topicId); command.Parameters.AddWithValue("companyId", run.CompanyId); command.Parameters.AddWithValue("runId", run.Id);
            command.Parameters.AddWithValue("title", synthesis?.Title?.Trim() ?? candidate.Rule.Title); command.Parameters.AddWithValue("summary", synthesis?.Summary?.Trim() ?? candidate.Rule.Action);
            command.Parameters.AddWithValue("category", candidate.Rule.Category); command.Parameters.AddWithValue("confidence", confidence);
            command.Parameters.AddWithValue("evidenceCount", candidate.Reports.Length); command.Parameters.AddWithValue("collaborators", collaborators);
            command.Parameters.AddWithValue("impact", Math.Min(100, 50 + candidate.Reports.Length * 5)); command.Parameters.AddWithValue("action", synthesis?.RecommendedAction?.Trim() ?? candidate.Rule.Action);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string evidenceSql = """
            INSERT INTO skopos_coach_topic_evidence (topic_id, report_id, relevance, excerpt)
            VALUES (@topicId, @reportId, @relevance, @excerpt) ON CONFLICT DO NOTHING
            """;
        foreach (var report in candidate.Reports)
        {
            await using var command = new NpgsqlCommand(evidenceSql, connection, transaction);
            command.Parameters.AddWithValue("topicId", topicId); command.Parameters.AddWithValue("reportId", report.Id);
            command.Parameters.AddWithValue("relevance", confidence); command.Parameters.AddWithValue("excerpt", Sanitize(report.Summary)[..Math.Min(280, Sanitize(report.Summary).Length)]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static bool Matches(ReportRow report, TopicRule rule)
    {
        var text = RemoveDiacritics($"{report.Summary} {report.InsightsJson}").ToLowerInvariant();
        return rule.Keywords.Any(text.Contains);
    }
    private static bool IsConfirmed(Candidate candidate) => MeetsConfirmationThreshold(candidate.Reports.Length, candidate.Reports.Select(x => x.OwnerUserId).Where(x => x.HasValue).Distinct().Count(), Confidence(candidate));
    internal static bool MeetsConfirmationThreshold(int evidenceCount, int collaboratorCount, int confidence) => evidenceCount >= 5 && collaboratorCount >= 2 && confidence >= 70;
    private static int Confidence(Candidate candidate) => Math.Min(95, 60 + candidate.Reports.Length * 3 + candidate.Reports.Select(x => x.SourceType).Distinct().Count() * 3);
    private static string Sanitize(string text) => SanitizeForCoach(text);
    internal static string SanitizeForCoach(string text) => PhoneRegex().Replace(EmailRegex().Replace(text, "[email]"), "[telefone]").Trim();
    private static string RemoveDiacritics(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark));
    private static async Task FailRunAsync(NpgsqlConnection connection, Guid id, string error, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE skopos_coach_runs SET status = 'failed', error_message = left(@error, 1000), completed_at = now(), updated_at = now() WHERE id = @id";
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("error", error); await command.ExecuteNonQueryAsync(cancellationToken);
    }
    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)] private static partial Regex EmailRegex();
    [GeneratedRegex(@"(?<!\d)(?:\+?55\s*)?(?:\(?\d{2}\)?\s*)?\d{4,5}[-\s]?\d{4}(?!\d)")] private static partial Regex PhoneRegex();
    private sealed record RunRow(Guid Id, Guid CompanyId, DateOnly From, DateOnly To, string GroupIds);
    private sealed record ReportRow(Guid Id, string SourceType, Guid? OwnerUserId, string Summary, string InsightsJson);
    private sealed record TopicRule(string Category, string Title, string Action, string[] Keywords);
    private sealed record Candidate(TopicRule Rule, ReportRow[] Reports);
}
