using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAi.Application;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.SkoposCoach;

public sealed class SkoposIndividualCoachProcessor(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    SkoposIndividualCoachClient client,
    ILogger<SkoposIndividualCoachProcessor> logger)
{
    private const string AgentKey = "skopos-individual-coach";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly CompetencyRule[] Rules =
    [
        new("service", "Qualidade do atendimento", ["atendimento", "demora", "resposta", "cliente", "experiencia", "abordagem"]),
        new("cadence", "Cadência e próximos passos", ["follow-up", "follow up", "retorno", "sem contato", "proximo passo", "pendente", "reativ"]),
        new("qualification", "Qualificação e descoberta", ["qualifica", "decisor", "necessidade", "diagnost", "dor", "urgencia"]),
        new("objections", "Quebra de objeções", ["objec", "resistencia", "recusa", "duvida", "barreira"]),
        new("proposal", "Proposta e negociação", ["proposta", "orcamento", "preco", "pagamento", "condicao", "negocia"]),
        new("product", "Domínio de produto", ["produto", "servico", "solucao", "beneficio", "entrega"]),
        new("execution", "Execução comercial", ["atividade", "meta", "efetividade", "produtiv", "atras", "estagn"])
    ];

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        while (await ClaimAsync(connection, cancellationToken) is { } plan)
        {
            try { await ProcessAsync(connection, plan, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Skopos Individual Coach plan {PlanId} failed.", plan.Id);
                await FailAsync(connection, plan.Id, exception.Message, cancellationToken);
            }
        }
    }

    private static async Task<PlanRequest?> ClaimAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE skopos_development_plans plan
            SET status = 'processing', attempt_count = attempt_count + 1, updated_at = now(), error_message = null
            WHERE plan.id = (
                SELECT pending.id FROM skopos_development_plans pending
                WHERE pending.status = 'pending' AND pending.attempt_count < 3
                ORDER BY pending.created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            RETURNING plan.id, plan.company_id, plan.user_id, plan.group_id, plan.date_from, plan.date_to
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetFieldValue<DateOnly>(4), reader.GetFieldValue<DateOnly>(5));
    }

    private async Task ProcessAsync(NpgsqlConnection connection, PlanRequest plan, CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(AgentKey, plan.CompanyId.ToString(), cancellationToken);
        if (!settings.IsActive) throw new InvalidOperationException("Skopos Individual Coach is inactive for this company.");
        var reports = await ReadReportsAsync(connection, plan, cancellationToken);
        var metrics = await ReadMetricsAsync(connection, plan, reports, cancellationToken);
        if (metrics.ReportCount < 5 || metrics.OpportunityCount < 3)
        {
            await MarkInsufficientAsync(connection, plan, metrics, cancellationToken);
            return;
        }

        var candidates = Rules.Select(rule => new CompetencyCandidate(rule, reports.Where(report => Matches(report.Summary, rule)).ToArray())).ToArray();
        var competencyScores = candidates.Select(candidate => new CompetencyScore(candidate.Rule.Key, candidate.Rule.Title, Score(candidate, metrics), candidate.Reports.Length)).ToArray();
        var context = await ReadCommercialContextAsync(connection, plan, cancellationToken);
        IndividualCoachResult? synthesis = null;
        try
        {
            synthesis = await client.AnalyzeAsync(settings, new
            {
                period = new { from = plan.From, to = plan.To },
                methodology = new
                {
                    minimumReports = 5,
                    minimumOpportunities = 3,
                    responseAttribution = "current_conversation_owner",
                    excludedActivityType = "agent-skopos",
                    contentPolicy = "sanitized_analytical_summaries_only"
                },
                metrics,
                competencies = candidates.Select(candidate => new
                {
                    key = candidate.Rule.Key,
                    title = candidate.Rule.Title,
                    deterministicScore = Score(candidate, metrics),
                    evidenceCount = candidate.Reports.Length,
                    samples = candidate.Reports.Take(5).Select(report => new
                    {
                        id = report.Id.ToString(),
                        source = report.SourceType,
                        summary = Truncate(SkoposCoachProjectionService.SanitizeForCoach(report.Summary), 600)
                    })
                }),
                commercialContext = JsonSerializer.Deserialize<object>(context, JsonOptions)
            }, plan.CompanyId.ToString(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Individual Coach synthesis failed; deterministic PDI will be used for {PlanId}.", plan.Id);
        }

        var allowedEvidenceIds = reports.Select(report => report.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = NormalizeItems(synthesis, candidates, competencyScores, allowedEvidenceIds);
        var strengths = NormalizeStrengths(synthesis, competencyScores);
        var limitations = new[]
        {
            "O tempo de resposta usa o responsável atual da conversa; não há autoria individual por mensagem.",
            "O tempo de resposta é tempo corrido e pode incluir períodos fora do expediente até que a empresa configure uma jornada comercial.",
            "Campanhas e anúncios acompanham as conversas atualmente atribuídas ao colaborador e servem apenas como contexto, não como nota de desempenho.",
            "Atividades automáticas do tipo agent-skopos foram excluídas dos indicadores de execução.",
            "A avaliação semântica usa resumos analíticos sanitizados, não mensagens, áudios ou transcrições brutas."
        };
        var assessmentId = Guid.NewGuid();
        var coverage = Coverage(metrics);
        var confidence = Confidence(metrics);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Instructions)));
        var summary = string.IsNullOrWhiteSpace(synthesis?.Summary)
            ? $"Avaliação baseada em {metrics.ReportCount} relatórios, {metrics.OpportunityCount} oportunidades e {metrics.SourceCount} fontes. O plano prioriza até três competências com evidências rastreáveis."
            : synthesis.Summary.Trim();
        var objective = string.IsNullOrWhiteSpace(synthesis?.Objective)
            ? "Elevar a consistência do atendimento e da execução comercial com ações mensuráveis no próximo ciclo."
            : synthesis.Objective.Trim();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertAssessmentAsync(connection, transaction, assessmentId, plan, metrics, coverage, confidence, competencyScores, strengths, items, limitations, settings.Model, fingerprint, cancellationToken);
        await InsertEvidenceAsync(connection, transaction, assessmentId, candidates, cancellationToken);
        await UpdatePlanAsync(connection, transaction, plan.Id, assessmentId, summary, objective, coverage, confidence, strengths, limitations, settings.Model, fingerprint, cancellationToken);
        await InsertItemsAsync(connection, transaction, plan, items, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<List<Report>> ReadReportsAsync(NpgsqlConnection connection, PlanRequest plan, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source_type, opportunity_id, report_summary
            FROM skopos_coach_agent_reports
            WHERE company_id = @companyId AND owner_user_id = @userId
              AND occurred_at >= @from::date AND occurred_at < (@to::date + interval '1 day')
            ORDER BY occurred_at DESC
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", plan.CompanyId);
        command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.AddWithValue("from", plan.From);
        command.Parameters.AddWithValue("to", plan.To);
        var reports = new List<Report>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) reports.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3)));
        return reports;
    }

    private static async Task<ObjectiveMetrics> ReadMetricsAsync(NpgsqlConnection connection, PlanRequest plan, IReadOnlyCollection<Report> reports, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH ordered AS (
                SELECT message.direction, message.message_at,
                       lag(message.direction) OVER (PARTITION BY message.conversation_id ORDER BY message.message_at) previous_direction,
                       lag(message.message_at) OVER (PARTITION BY message.conversation_id ORDER BY message.message_at) previous_at
                FROM whatsapp_messages message
                JOIN whatsapp_conversations conversation ON conversation.id = message.conversation_id
                WHERE message.company_id = @companyId AND conversation.owner_user_id = @userId
                  AND message.message_at >= @from::date AND message.message_at < (@to::date + interval '1 day')
            ), responses AS (
                SELECT extract(epoch FROM (message_at - previous_at)) seconds FROM ordered
                WHERE direction = 'outgoing' AND previous_direction = 'incoming' AND message_at >= previous_at
            ), activity_metrics AS (
                SELECT count(*)::int activity_count,
                       count(*) FILTER (WHERE status = 'done')::int completed_count,
                       count(*) FILTER (WHERE status <> 'done' AND date_at < now())::int overdue_count
                FROM activities WHERE company_id = @companyId AND owner_user_id = @userId
                  AND activity_type <> 'agent-skopos' AND date_at >= @from::date AND date_at < (@to::date + interval '1 day')
            )
            SELECT (SELECT count(*)::int FROM responses) response_count,
                   (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY seconds)::int FROM responses) median_seconds,
                   (SELECT percentile_cont(0.9) WITHIN GROUP (ORDER BY seconds)::int FROM responses) p90_seconds,
                   activity_count, completed_count, overdue_count
            FROM activity_metrics
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", plan.CompanyId); command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.AddWithValue("from", plan.From); command.Parameters.AddWithValue("to", plan.To);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(
            reports.Count,
            reports.Select(report => report.OpportunityId).Where(id => id.HasValue).Distinct().Count(),
            reports.Select(report => report.SourceType).Distinct().Count(),
            reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    private static async Task<string> ReadCommercialContextAsync(NpgsqlConnection connection, PlanRequest plan, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT jsonb_build_object(
              'products', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
                SELECT product.name, count(DISTINCT report.opportunity_id)::int opportunity_count
                FROM skopos_coach_agent_reports report
                JOIN opportunity_products link ON link.opportunity_id = report.opportunity_id
                JOIN products product ON product.id = link.product_id
                WHERE report.company_id = @companyId AND report.owner_user_id = @userId
                  AND report.occurred_at >= @from::date AND report.occurred_at < (@to::date + interval '1 day')
                GROUP BY product.name ORDER BY count(DISTINCT report.opportunity_id) DESC LIMIT 10) item),
              'ads', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
                SELECT attribution.source_platform, coalesce(attribution.campaign_name, attribution.utm_campaign, 'Sem campanha') campaign,
                       count(*)::int interactions
                FROM commercial_attribution_events attribution
                JOIN whatsapp_conversations conversation ON conversation.id = attribution.conversation_id
                WHERE attribution.company_id = @companyId AND conversation.owner_user_id = @userId
                  AND attribution.captured_at >= @from::date AND attribution.captured_at < (@to::date + interval '1 day')
                GROUP BY attribution.source_platform, coalesce(attribution.campaign_name, attribution.utm_campaign, 'Sem campanha')
                ORDER BY count(*) DESC LIMIT 10) item))
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", plan.CompanyId); command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.AddWithValue("from", plan.From); command.Parameters.AddWithValue("to", plan.To);
        return (string)(await command.ExecuteScalarAsync(cancellationToken) ?? "{}");
    }

    private static IReadOnlyCollection<NormalizedItem> NormalizeItems(IndividualCoachResult? synthesis, IReadOnlyCollection<CompetencyCandidate> candidates, IReadOnlyCollection<CompetencyScore> scores, HashSet<string> allowedEvidenceIds)
    {
        var validKeys = Rules.Select(rule => rule.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelItems = synthesis?.Items
            .Where(item => validKeys.Contains(item.CompetencyKey))
            .GroupBy(item => item.CompetencyKey, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(3)
            .Select(item =>
            {
                var deterministicScore = scores.First(score => score.Key.Equals(item.CompetencyKey, StringComparison.OrdinalIgnoreCase)).Score;
                var baseline = Math.Clamp((int)Math.Round((Math.Clamp(item.BaselineScore, 0, 100) + deterministicScore) / 2d), 0, 100);
                var evidence = item.EvidenceIds.Where(allowedEvidenceIds.Contains).Distinct().Take(5).ToArray();
                if (evidence.Length == 0)
                    evidence = candidates.First(candidate => candidate.Rule.Key.Equals(item.CompetencyKey, StringComparison.OrdinalIgnoreCase)).Reports.Take(5).Select(report => report.Id.ToString()).ToArray();
                return new NormalizedItem(item.CompetencyKey, Truncate(item.Title, 140), baseline, Math.Clamp(Math.Max(item.TargetScore, baseline + 5), 0, 100), Truncate(item.Action, 500), Truncate(item.Measurement, 500), Truncate(item.Resource, 500), Math.Clamp(item.DueInDays, 7, 90), evidence);
            })
            .ToArray() ?? [];
        if (modelItems.Length > 0) return modelItems;
        return candidates.Where(candidate => candidate.Reports.Length > 0)
            .OrderBy(candidate => scores.First(score => score.Key == candidate.Rule.Key).Score).Take(3)
            .Select(candidate =>
            {
                var score = scores.First(item => item.Key == candidate.Rule.Key).Score;
                return new NormalizedItem(candidate.Rule.Key, candidate.Rule.Title, score, Math.Min(90, score + 15), DefaultAction(candidate.Rule.Key), DefaultMeasurement(candidate.Rule.Key), "Role-play semanal e revisão de evidências com o gestor.", 30, candidate.Reports.Take(5).Select(report => report.Id.ToString()).ToArray());
            }).DefaultIfEmpty(new NormalizedItem("execution", "Execução comercial", 70, 85, DefaultAction("execution"), DefaultMeasurement("execution"), "Revisão semanal com o gestor.", 30, [])).ToArray();
    }

    private static IReadOnlyCollection<IndividualCoachStrength> NormalizeStrengths(IndividualCoachResult? synthesis, IReadOnlyCollection<CompetencyScore> scores)
    {
        var validKeys = Rules.Select(rule => rule.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strengths = synthesis?.Strengths.Where(item => validKeys.Contains(item.Key)).Take(3)
            .Select(item =>
            {
                var deterministicScore = scores.First(score => score.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)).Score;
                return item with { Score = Math.Clamp((int)Math.Round((Math.Clamp(item.Score, 0, 100) + deterministicScore) / 2d), 0, 100), Title = Truncate(item.Title, 140), Summary = Truncate(item.Summary, 500) };
            }).ToArray() ?? [];
        if (strengths.Length > 0) return strengths;
        var best = scores.OrderByDescending(item => item.Score).First();
        return [new(best.Key, best.Title, "Competência relativamente mais consistente dentro da amostra disponível; deve ser preservada durante o ciclo.", best.Score)];
    }

    private static async Task InsertAssessmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, PlanRequest plan, ObjectiveMetrics metrics, int coverage, int confidence, IReadOnlyCollection<CompetencyScore> scores, IReadOnlyCollection<IndividualCoachStrength> strengths, IReadOnlyCollection<NormalizedItem> items, IReadOnlyCollection<string> limitations, string model, string fingerprint, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO skopos_coach_user_assessments
                (id, company_id, user_id, group_id, date_from, date_to, status, report_count, opportunity_count,
                 source_count, coverage_score, confidence, median_response_seconds, p90_response_seconds,
                 activity_count, completed_activity_count, overdue_activity_count, competency_scores_json,
                 strengths_json, focus_areas_json, limitations_json, model, prompt_fingerprint, completed_at)
            VALUES (@id, @companyId, @userId, @groupId, @from, @to, 'completed', @reports, @opportunities,
                    @sources, @coverage, @confidence, @median, @p90, @activities, @completed, @overdue,
                    @scores::jsonb, @strengths::jsonb, @items::jsonb, @limitations::jsonb, @model, @fingerprint, now())
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("companyId", plan.CompanyId); command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.Add("groupId", NpgsqlDbType.Uuid).Value = plan.GroupId is null ? DBNull.Value : plan.GroupId.Value; command.Parameters.AddWithValue("from", plan.From); command.Parameters.AddWithValue("to", plan.To);
        command.Parameters.AddWithValue("reports", metrics.ReportCount); command.Parameters.AddWithValue("opportunities", metrics.OpportunityCount); command.Parameters.AddWithValue("sources", metrics.SourceCount);
        command.Parameters.AddWithValue("coverage", coverage); command.Parameters.AddWithValue("confidence", confidence); command.Parameters.Add("median", NpgsqlDbType.Integer).Value = metrics.MedianResponseSeconds is null ? DBNull.Value : metrics.MedianResponseSeconds.Value;
        command.Parameters.Add("p90", NpgsqlDbType.Integer).Value = metrics.P90ResponseSeconds is null ? DBNull.Value : metrics.P90ResponseSeconds.Value; command.Parameters.AddWithValue("activities", metrics.ActivityCount);
        command.Parameters.AddWithValue("completed", metrics.CompletedActivityCount); command.Parameters.AddWithValue("overdue", metrics.OverdueActivityCount);
        command.Parameters.AddWithValue("scores", JsonSerializer.Serialize(scores, JsonOptions)); command.Parameters.AddWithValue("strengths", JsonSerializer.Serialize(strengths, JsonOptions));
        command.Parameters.AddWithValue("items", JsonSerializer.Serialize(items, JsonOptions)); command.Parameters.AddWithValue("limitations", JsonSerializer.Serialize(limitations, JsonOptions));
        command.Parameters.AddWithValue("model", model); command.Parameters.AddWithValue("fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEvidenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid assessmentId, IEnumerable<CompetencyCandidate> candidates, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO skopos_coach_user_assessment_evidence (assessment_id, report_id, competency_key, relevance, excerpt) VALUES (@assessment, @report, @key, @relevance, @excerpt) ON CONFLICT DO NOTHING";
        foreach (var candidate in candidates)
        foreach (var report in candidate.Reports.Take(10))
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("assessment", assessmentId); command.Parameters.AddWithValue("report", report.Id); command.Parameters.AddWithValue("key", candidate.Rule.Key);
            command.Parameters.AddWithValue("relevance", Math.Min(95, 65 + candidate.Reports.Length * 3)); command.Parameters.AddWithValue("excerpt", Truncate(SkoposCoachProjectionService.SanitizeForCoach(report.Summary), 280));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdatePlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid planId, Guid assessmentId, string summary, string objective, int coverage, int confidence, IReadOnlyCollection<IndividualCoachStrength> strengths, IReadOnlyCollection<string> limitations, string model, string fingerprint, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE skopos_development_plans SET assessment_id = @assessment, status = 'draft', title = 'Plano de desenvolvimento individual',
                summary = @summary, objective = @objective, start_date = current_date, due_date = current_date + 30,
                coverage_score = @coverage, confidence = @confidence, strengths_json = @strengths::jsonb,
                limitations_json = @limitations::jsonb, model = @model, prompt_fingerprint = @fingerprint,
                error_message = null, updated_at = now() WHERE id = @id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("assessment", assessmentId); command.Parameters.AddWithValue("summary", summary); command.Parameters.AddWithValue("objective", objective);
        command.Parameters.AddWithValue("coverage", coverage); command.Parameters.AddWithValue("confidence", confidence); command.Parameters.AddWithValue("strengths", JsonSerializer.Serialize(strengths, JsonOptions));
        command.Parameters.AddWithValue("limitations", JsonSerializer.Serialize(limitations, JsonOptions)); command.Parameters.AddWithValue("model", model); command.Parameters.AddWithValue("fingerprint", fingerprint); command.Parameters.AddWithValue("id", planId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertItemsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PlanRequest plan, IEnumerable<NormalizedItem> items, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO skopos_development_plan_items
                (plan_id, competency_key, title, baseline_score, target_score, current_score, action, measurement, resource, due_date, evidence_ids)
            VALUES (@plan, @key, @title, @baseline, @target, @baseline, @action, @measurement, @resource, @due, @evidence::jsonb)
            """;
        foreach (var item in items)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("plan", plan.Id); command.Parameters.AddWithValue("key", item.CompetencyKey); command.Parameters.AddWithValue("title", item.Title);
            command.Parameters.AddWithValue("baseline", item.BaselineScore); command.Parameters.AddWithValue("target", item.TargetScore); command.Parameters.AddWithValue("action", item.Action);
            command.Parameters.AddWithValue("measurement", item.Measurement); command.Parameters.AddWithValue("resource", item.Resource); command.Parameters.AddWithValue("due", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(item.DueInDays));
            command.Parameters.AddWithValue("evidence", JsonSerializer.Serialize(item.EvidenceIds, JsonOptions)); await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task MarkInsufficientAsync(NpgsqlConnection connection, PlanRequest plan, ObjectiveMetrics metrics, CancellationToken cancellationToken)
    {
        const string limitation = "A amostra deixou de atender ao mínimo de 5 relatórios e 3 oportunidades antes do processamento.";
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var assessmentId = Guid.NewGuid();
        await using (var command = new NpgsqlCommand("INSERT INTO skopos_coach_user_assessments (id, company_id, user_id, group_id, date_from, date_to, status, report_count, opportunity_count, source_count, limitations_json, completed_at) VALUES (@id,@company,@user,@group,@from,@to,'insufficient',@reports,@opportunities,@sources,@limitations::jsonb,now())", connection, transaction))
        {
            command.Parameters.AddWithValue("id", assessmentId); command.Parameters.AddWithValue("company", plan.CompanyId); command.Parameters.AddWithValue("user", plan.UserId); command.Parameters.Add("group", NpgsqlDbType.Uuid).Value = plan.GroupId is null ? DBNull.Value : plan.GroupId.Value;
            command.Parameters.AddWithValue("from", plan.From); command.Parameters.AddWithValue("to", plan.To); command.Parameters.AddWithValue("reports", metrics.ReportCount); command.Parameters.AddWithValue("opportunities", metrics.OpportunityCount); command.Parameters.AddWithValue("sources", metrics.SourceCount); command.Parameters.AddWithValue("limitations", JsonSerializer.Serialize(new[] { limitation }, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand("UPDATE skopos_development_plans SET assessment_id=@assessment,status='failed',error_message=@error,updated_at=now() WHERE id=@id", connection, transaction))
        {
            command.Parameters.AddWithValue("assessment", assessmentId); command.Parameters.AddWithValue("error", limitation); command.Parameters.AddWithValue("id", plan.Id); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task FailAsync(NpgsqlConnection connection, Guid id, string error, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE skopos_development_plans SET status='failed', error_message=left(@error,1000), updated_at=now() WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("error", error); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool Matches(string summary, CompetencyRule rule)
    {
        var normalized = RemoveDiacritics(summary).ToLowerInvariant();
        return rule.Keywords.Any(normalized.Contains);
    }
    private static string RemoveDiacritics(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark));
    private static int Score(CompetencyCandidate candidate, ObjectiveMetrics metrics)
    {
        var issuePenalty = metrics.ReportCount == 0 ? 0 : Math.Min(45, (int)Math.Round(candidate.Reports.Length * 100d / metrics.ReportCount * 0.8));
        var score = 82 - issuePenalty;
        if (candidate.Rule.Key == "execution" && metrics.ActivityCount > 0)
            score = (int)Math.Round(score * .55 + metrics.CompletedActivityCount * 100d / metrics.ActivityCount * .45) - Math.Min(15, metrics.OverdueActivityCount * 3);
        return Math.Clamp(score, 25, 95);
    }
    internal static int Coverage(ObjectiveMetrics metrics) => Math.Min(100, Math.Min(metrics.ReportCount, 10) * 4 + Math.Min(metrics.OpportunityCount, 3) * 10 + Math.Min(metrics.SourceCount, 2) * 10 + (metrics.ResponseCount > 0 ? 10 : 0));
    internal static int Confidence(ObjectiveMetrics metrics) => Math.Min(95, 35 + Math.Min(metrics.ReportCount, 10) * 3 + Math.Min(metrics.OpportunityCount, 3) * 5 + Math.Min(metrics.SourceCount, 2) * 5 + (metrics.ResponseCount > 0 ? 5 : 0));
    private static string Truncate(string? value, int length) { var normalized = value?.Trim() ?? ""; return normalized.Length <= length ? normalized : normalized[..length]; }
    private static string DefaultAction(string key) => key switch { "cadence" => "Registrar o próximo passo ao final de cada atendimento e executar os retornos no prazo combinado.", "qualification" => "Aplicar um roteiro curto de necessidade, urgência, autoridade e critério de decisão antes de avançar.", "objections" => "Praticar escuta, validação e resposta baseada em valor para as objeções recorrentes da amostra.", "proposal" => "Apresentar proposta conectando valor, condição e próximo compromisso verificável.", "product" => "Revisar os produtos mais presentes na carteira e praticar sua conexão com necessidades reais.", "service" => "Reduzir esperas e tornar cada resposta clara, contextual e orientada ao próximo passo.", _ => "Revisar a carteira diariamente e concluir atividades comerciais não automáticas dentro do prazo." };
    private static string DefaultMeasurement(string key) => key switch { "cadence" => "Ao menos 90% dos atendimentos avaliados com próximo passo registrado.", "qualification" => "Quatro critérios de qualificação registrados em 80% das oportunidades novas.", "objections" => "Registrar objeção, resposta aplicada e reação do cliente em cinco casos revisados.", "proposal" => "Toda proposta enviada com data de retorno e compromisso seguinte definidos.", "service" => "Reduzir a mediana e o P90 de resposta em relação à linha de base do período.", _ => "Revisão semanal de cinco evidências com evolução observável no indicador da competência." };

    internal sealed record ObjectiveMetrics(int ReportCount, int OpportunityCount, int SourceCount, int ResponseCount, int? MedianResponseSeconds, int? P90ResponseSeconds, int ActivityCount, int CompletedActivityCount, int OverdueActivityCount);
    private sealed record PlanRequest(Guid Id, Guid CompanyId, Guid UserId, Guid? GroupId, DateOnly From, DateOnly To);
    private sealed record Report(Guid Id, string SourceType, Guid? OpportunityId, string Summary);
    private sealed record CompetencyRule(string Key, string Title, string[] Keywords);
    private sealed record CompetencyCandidate(CompetencyRule Rule, Report[] Reports);
    private sealed record CompetencyScore(string Key, string Title, int Score, int EvidenceCount);
    private sealed record NormalizedItem(string CompetencyKey, string Title, int BaselineScore, int TargetScore, string Action, string Measurement, string Resource, int DueInDays, IReadOnlyCollection<string> EvidenceIds);
}
