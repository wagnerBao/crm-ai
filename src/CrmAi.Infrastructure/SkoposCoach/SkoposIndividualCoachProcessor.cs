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
    private static readonly CriterionMapping[] CriterionMappings =
    [
        new("objections", ["objection_handling", "objections", "objecoes", "tratamento_de_objecoes", "quebra_de_objecoes"]),
        new("qualification", ["needs_discovery", "decision_process", "qualification", "discovery", "qualificacao", "descoberta", "processo_de_decisao"]),
        new("cadence", ["next_step", "follow_up", "cadence", "proximo_passo", "cadencia"]),
        new("proposal", ["proposal_clarity", "negotiation", "value_building", "proposal", "proposta", "negociacao", "construcao_de_valor"]),
        new("product", ["product_knowledge", "product", "dominio_de_produto", "conhecimento_de_produto"]),
        new("execution", ["playbook_adherence", "execution", "activity_execution", "aderencia_ao_playbook", "execucao_comercial", "execucao_de_atividades"]),
        new("service", ["opening_connection", "customer_service", "service", "rapport", "communication", "response_time", "response_quality", "quality_of_service", "abertura_e_conexao", "qualidade_do_atendimento", "tempo_de_resposta", "qualidade_da_resposta", "atendimento"])
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
        settings = CommercialCoachingPolicy.ForAnalysis(settings);
        settings = settings with { ContextInstructions = settings.ContextInstructions + "\n\n" + SkoposIndividualCoachClient.DevelopmentInstructions };
        if (!settings.IsActive) throw new InvalidOperationException("Skopos Individual Coach is inactive for this company.");
        var reports = await ReadReportsAsync(connection, plan, cancellationToken);
        var metrics = await ReadMetricsAsync(connection, plan, reports, cancellationToken);
        if (metrics.ReportCount < 5 || metrics.OpportunityCount < 3)
        {
            await MarkInsufficientAsync(connection, plan, metrics, cancellationToken);
            return;
        }

        var scorecardSignals = await ReadScorecardSignalsAsync(connection, plan, cancellationToken);
        var candidates = BuildCandidates(reports, scorecardSignals);
        var competencyScores = candidates.Select(candidate => new CompetencyScore(candidate.Rule.Key, candidate.Rule.Title, Score(candidate, metrics), candidate.Reports.Length, candidate.Signals.Length > 0 ? "scorecard" : "qualitative")).ToArray();
        var context = await ReadCommercialContextAsync(connection, plan, cancellationToken);
        IndividualCoachResult? synthesis = null;
        try
        {
            synthesis = await client.AnalyzeAsync(settings, new
            {
                period = new { from = plan.From, to = plan.To },
                dataContext = new
                {
                    version = "commercial-development-v1",
                    coverage = Coverage(metrics),
                    confidence = Confidence(metrics),
                    definitions = new
                    {
                        responseTime = "Espera desde a primeira entrada sem resposta; autoria autenticada e calendario da equipe. Mensagens automaticas nao contam como resposta humana.",
                        execution = "Atividades comerciais no periodo, excluindo o tipo automatico agent-skopos.",
                        competencies = "Nota ponderada dos scorecards correntes com evidencia; revisao humana prevalece. Resumos legados sao fallback.",
                        targets = "Metas propostas para desenvolvimento, sujeitas a revisao humana; nao sao previsao de venda."
                    }
                },
                methodology = new
                {
                    minimumReports = 5,
                    minimumOpportunities = 3,
                    responseAttribution = "authenticated_sender_only",
                    excludedActivityType = "agent-skopos",
                    contentPolicy = "structured_scorecards_first_sanitized_legacy_summaries_fallback",
                    scorecardPolicy = new
                    {
                        currentVersionOnly = true,
                        requiresEvidence = true,
                        reviewedScoreOverridesAiScore = true,
                        reviewedEvidenceWeightMultiplier = 2
                    }
                },
                operationalContext = metrics.OperationalContext,
                metrics,
                scorecardEvolution = scorecardSignals.GroupBy(signal => new { signal.CriterionKey, signal.TemplateKey, signal.TemplateVersion })
                    .Select(group => new { group.Key, observations = group.OrderBy(signal => signal.Report.OccurredAt)
                        .Select(signal => new { signal.Report.OccurredAt, signal.Score, signal.Reviewed, reportId = signal.Report.Id }).TakeLast(20) }),
                competencies = candidates.Select(candidate => new
                {
                    key = candidate.Rule.Key,
                    title = candidate.Rule.Title,
                    deterministicScore = Score(candidate, metrics),
                    evidenceCount = candidate.Reports.Length,
                    scoreOrigin = candidate.Signals.Length > 0
                        ? candidate.Signals.Any(signal => signal.Reviewed) ? "reviewed_scorecard" : "ai_scorecard"
                        : "legacy_summary",
                    reviewedEvidenceCount = candidate.Signals.Where(signal => signal.Reviewed).Select(signal => signal.Report.Id).Distinct().Count(),
                    samples = candidate.Reports.Take(5).Select(report => new
                    {
                        id = report.Id.ToString(),
                        source = report.SourceType,
                        summary = Truncate(SkoposCoachProjectionService.SanitizeForCoach(
                            SelectEvidenceExcerpt(candidate, report)), 600)
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
        var limitations = BuildLimitations(scorecardSignals, candidates, metrics).ToList();
        if (synthesis is null) limitations.Add("A síntese por IA estava indisponível. Este roteiro inicial foi preparado por regras e precisa de contextualização pelo gestor.");
        var assessmentId = Guid.NewGuid();
        var coverage = Coverage(metrics);
        var confidence = Confidence(metrics);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Instructions)));
        var scorecardReportCount = scorecardSignals.Select(signal => signal.Report.Id).Distinct().Count();
        var reviewedScorecardCount = scorecardSignals.Where(signal => signal.Reviewed).Select(signal => signal.Report.Id).Distinct().Count();
        var summary = string.IsNullOrWhiteSpace(synthesis?.Summary)
            ? $"Avaliação baseada em {metrics.ReportCount} relatórios, {metrics.OpportunityCount} oportunidades, {metrics.SourceCount} fontes e {scorecardReportCount} scorecards válidos ({reviewedScorecardCount} revisados). O plano prioriza até três competências com evidências rastreáveis."
            : synthesis.Summary.Trim();
        var objective = string.IsNullOrWhiteSpace(synthesis?.Objective)
            ? "Elevar a consistência do atendimento e da execução comercial com ações mensuráveis no próximo ciclo."
            : synthesis.Objective.Trim();

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertAssessmentAsync(connection, transaction, assessmentId, plan, metrics, coverage, confidence, competencyScores, strengths, items, limitations, settings.Model, fingerprint, cancellationToken);
        await InsertEvidenceAsync(connection, transaction, assessmentId, candidates, cancellationToken);
        await UpdatePlanAsync(connection, transaction, plan.Id, assessmentId, summary, objective, coverage, confidence, strengths, limitations, settings.Model, fingerprint, items.Select(item => item.DueInDays).DefaultIfEmpty(30).Max(), BuildMethodology(metrics), cancellationToken);
        await InsertItemsAsync(connection, transaction, plan, items, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<List<Report>> ReadReportsAsync(NpgsqlConnection connection, PlanRequest plan, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source_type, opportunity_id, report_summary, occurred_at
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
        while (await reader.ReadAsync(cancellationToken)) reports.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetDateTime(4)));
        return reports;
    }

    private static async Task<ScorecardSignal[]> ReadScorecardSignalsAsync(NpgsqlConnection connection, PlanRequest plan, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT report.id, report.source_type, report.opportunity_id, report.report_summary,
                   item.criterion_key, item.criterion_title, item.weight,
                   CASE WHEN scorecard.status = 'reviewed'
                        THEN coalesce(item.reviewed_score, item.ai_score)
                        ELSE item.ai_score END effective_score,
                   item.confidence_score, (scorecard.status = 'reviewed' AND item.reviewed_score IS NOT NULL) reviewed,
                   item.justification, criterion.competency_key, report.occurred_at, scorecard.template_key, scorecard.template_version
            FROM conversation_scorecards scorecard
            LEFT JOIN conversation_analysis_results analysis
              ON analysis.id = scorecard.analysis_result_id
            JOIN conversation_scorecard_items item ON item.scorecard_id = scorecard.id
            LEFT JOIN conversation_scorecard_criteria criterion ON criterion.template_id=scorecard.template_id AND criterion.criterion_key=item.criterion_key
            JOIN skopos_coach_agent_reports report
              ON report.company_id = scorecard.company_id
             AND report.owner_user_id = @userId
             AND (
                  (scorecard.source_kind <> 'whatsapp_conversation'
                   AND analysis.is_current
                   AND report.source_type = 'meeting'
                   AND report.source_id = scorecard.recording_id)
                  OR
                  (scorecard.source_kind = 'whatsapp_conversation'
                   AND scorecard.is_current
                   AND report.source_type = 'whatsapp'
                   AND report.source_id = scorecard.whatsapp_analysis_run_id)
             )
            WHERE scorecard.company_id = @companyId
              AND scorecard.evaluated_user_id = @userId
              AND scorecard.status <> 'invalidated'
              AND report.occurred_at >= @from::date
              AND report.occurred_at < (@to::date + interval '1 day')
              AND item.confidence_score > 0
              AND jsonb_typeof(item.evidence_json) = 'array'
              AND jsonb_array_length(item.evidence_json) > 0
            ORDER BY report.occurred_at DESC, item.criterion_key
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", plan.CompanyId);
        command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.AddWithValue("from", plan.From);
        command.Parameters.AddWithValue("to", plan.To);
        var signals = new List<ScorecardSignal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var report = new Report(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetDateTime(12));
            signals.Add(new(
                report,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDecimal(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetBoolean(9),
                reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetGuid(13), reader.GetInt32(14)));
        }
        return signals.ToArray();
    }

    private static async Task<ObjectiveMetrics> ReadMetricsAsync(NpgsqlConnection connection, PlanRequest plan, IReadOnlyCollection<Report> reports, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH responses AS MATERIALIZED (SELECT response_seconds FROM skopos_response_samples(@companyId,@from,@to,@userId))
            SELECT (SELECT count(*)::int FROM responses),
                (SELECT percentile_cont(0.5) WITHIN GROUP(ORDER BY response_seconds)::int FROM responses),
                (SELECT percentile_cont(0.9) WITHIN GROUP(ORDER BY response_seconds)::int FROM responses),
                activity_count,completed_activity_count,overdue_activity_count,
                skopos_operational_context(@companyId,@userId,@from,@to)::text
            FROM skopos_activity_metrics(@companyId,@from,@to,@userId)
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
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), JsonSerializer.Deserialize<JsonElement>(reader.GetString(6)));
    }

    private static async Task<string> ReadCommercialContextAsync(NpgsqlConnection connection, PlanRequest plan, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT jsonb_build_object(
              'previousCycles', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
                SELECT previous.date_from, previous.date_to, previous.status, previous.objective,
                  (SELECT coalesce(jsonb_agg(jsonb_build_object('competencyKey', item.competency_key,
                    'baseline', item.baseline_score, 'current', item.current_score, 'target', item.target_score,
                    'action', item.action, 'status', item.status)), '[]'::jsonb)
                   FROM skopos_development_plan_items item WHERE item.plan_id = previous.id) items
                FROM skopos_development_plans previous
                WHERE previous.company_id = @companyId AND previous.user_id = @userId AND previous.id <> @planId
                  AND previous.status IN ('completed', 'cancelled')
                ORDER BY previous.created_at DESC LIMIT 2) item),
              'checkoutGoals', (SELECT coalesce(jsonb_agg(item), '[]'::jsonb) FROM (
                SELECT snapshot.snapshot_date, person -> 'goals' goals,
                       person -> 'planned' planned, person -> 'executed' executed
                FROM daily_checkout_snapshots snapshot
                CROSS JOIN LATERAL jsonb_array_elements(coalesce(snapshot.payload_json -> 'tables' -> 'performance', '[]'::jsonb)) person
                WHERE snapshot.company_id = @companyId AND person ->> 'id' = @userId::text
                  AND snapshot.snapshot_date BETWEEN @from AND @to
                ORDER BY snapshot.snapshot_date DESC LIMIT 7) item),
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
                WHERE attribution.company_id = @companyId AND attribution.owner_at_capture_user_id = @userId
                  AND attribution.captured_at >= @from::date AND attribution.captured_at < (@to::date + interval '1 day')
                GROUP BY attribution.source_platform, coalesce(attribution.campaign_name, attribution.utm_campaign, 'Sem campanha')
                ORDER BY count(*) DESC LIMIT 10) item))
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", plan.CompanyId); command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.AddWithValue("planId", plan.Id);
        command.Parameters.AddWithValue("from", plan.From); command.Parameters.AddWithValue("to", plan.To);
        return (string)(await command.ExecuteScalarAsync(cancellationToken) ?? "{}");
    }

    private static CompetencyCandidate[] BuildCandidates(IReadOnlyCollection<Report> reports, IReadOnlyCollection<ScorecardSignal> signals)
    {
        var scoredReportIds = signals.Select(signal => signal.Report.Id).ToHashSet();
        var legacyReports = reports.Where(report => !scoredReportIds.Contains(report.Id)).ToArray();
        return Rules.Select(rule =>
        {
            var structured = signals
                .Where(signal => string.Equals(MapCriterionToCompetency(signal.CriterionKey, signal.CriterionTitle, signal.CompetencyKey), rule.Key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var evidence = structured.Length > 0
                ? structured.Select(signal => signal.Report).DistinctBy(report => report.Id).ToArray()
                : legacyReports.Where(report => Matches(report.Summary, rule)).ToArray();
            return new CompetencyCandidate(rule, evidence, structured);
        }).ToArray();
    }

    internal static string? MapCriterionToCompetency(string criterionKey, string criterionTitle, string? explicitKey = null)
    {
        if (explicitKey == "none") return null;
        if (explicitKey is not null) return Rules.Any(rule => rule.Key == explicitKey) ? explicitKey : null;
        var normalized = NormalizeCriterion($"{criterionKey} {criterionTitle}");
        foreach (var mapping in CriterionMappings)
            if (mapping.Aliases.Any(alias => ContainsCriterionToken(normalized, alias))) return mapping.CompetencyKey;
        return null;
    }

    private static string NormalizeCriterion(string value)
    {
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousSeparator = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousSeparator = false;
            }
            else if (!previousSeparator)
            {
                builder.Append('_');
                previousSeparator = true;
            }
        }
        return builder.ToString().Trim('_');
    }

    private static bool ContainsCriterionToken(string normalized, string alias) =>
        normalized.Equals(alias, StringComparison.Ordinal) ||
        normalized.StartsWith($"{alias}_", StringComparison.Ordinal) ||
        normalized.EndsWith($"_{alias}", StringComparison.Ordinal) ||
        normalized.Contains($"_{alias}_", StringComparison.Ordinal);

    private static string SelectEvidenceExcerpt(CompetencyCandidate candidate, Report report)
    {
        var structured = candidate.Signals
            .Where(signal => signal.Report.Id == report.Id)
            .OrderByDescending(signal => signal.Reviewed)
            .ThenBy(signal => signal.Score)
            .ThenByDescending(signal => signal.Confidence)
            .FirstOrDefault();
        return structured?.Justification ?? report.Summary;
    }

    internal static string[] BuildMethodology(ObjectiveMetrics metrics) =>
    [
        "A autoria individual vem do usuário autenticado no envio. O responsável atual não substitui autoria desconhecida.",
        metrics.OperationalContext is { } context && context.TryGetProperty("clockBasis", out var basis) && basis.GetString() == "business"
            ? "O tempo de resposta conta somente a jornada comercial configurada, descontando dias sem expediente e feriados."
            : "O relógio utilizado é tempo corrido; nenhuma jornada comercial foi presumida.",
        "A espera começa na primeira entrada sem resposta do episódio. Mensagens inválidas e respostas automáticas ficam fora da amostra humana.",
        "Análises automáticas, tarefas de revisão de follow-up e conclusões sem autoria comprovada ficam separadas da execução humana.",
        "A nota revisada de cada critério prevalece sobre a IA. Cada interação usa o scorecard corrente, sem duplicar versões substituídas.",
        "Evolução só deve ser comparada dentro do mesmo critério, template e versão. Resumos sem scorecard fornecem contexto qualitativo, sem nota.",
        "Campanhas são contexto comercial. O vínculo usa o responsável observado na captura do evento; registros antigos sem esse vínculo não são atribuídos retroativamente."
    ];

    private static string[] BuildLimitations(IReadOnlyCollection<ScorecardSignal> signals, IReadOnlyCollection<CompetencyCandidate> candidates, ObjectiveMetrics metrics)
    {
        var limitations = new List<string>();
        if (metrics.ResponseCount == 0) limitations.Add("Não há respostas com autoria individual comprovada no período. O histórico sem autoria não foi atribuído ao responsável atual.");
        if (metrics.OperationalContext is { } context)
        {
            if (context.TryGetProperty("clockBasis", out var basis) && basis.GetString() == "elapsed")
                limitations.Add("O cálculo usa tempo corrido: não há jornada comercial habilitada na política da equipe.");
            if (context.TryGetProperty("followupReady", out var ready) && !ready.GetBoolean())
                limitations.Add("A fila de follow-up está desabilitada, em atualização ou sem projeção recente; não foi interpretada como fila sem pendências.");
            if (context.TryGetProperty("unknownCampaignEvents", out var campaigns) && campaigns.GetInt32() > 0)
                limitations.Add($"{campaigns.GetInt32()} eventos de campanha não têm responsável observado na captura; o vínculo histórico não foi reconstruído pelo responsável atual.");
            if (context.TryGetProperty("unknownResponseSamples", out var unknown) && unknown.GetInt32() > 0)
                limitations.Add($"{unknown.GetInt32()} respostas em conversas atualmente vinculadas ao colaborador não possuem autoria comprovada e ficaram fora da métrica individual.");
        }
        if (signals.Count > 0)
        {
            if (candidates.Any(candidate => candidate.Signals.Length == 0 && candidate.Reports.Length > 0))
                limitations.Add("Competências sem scorecard têm somente contexto qualitativo; os resumos legados não geraram nota numérica.");
            if (signals.Any(signal => MapCriterionToCompetency(signal.CriterionKey, signal.CriterionTitle, signal.CompetencyKey) is null))
                limitations.Add("Critérios personalizados sem correspondência com o catálogo atual foram preservados no scorecard, mas não influenciaram o PDI.");
        }
        else
        {
            limitations.Add("Não havia scorecards estruturados válidos no período. O roteiro usa contexto qualitativo e não atribui notas a competências sem cobertura.");
        }
        return limitations.ToArray();
    }

    private static IReadOnlyCollection<NormalizedItem> NormalizeItems(IndividualCoachResult? synthesis, IReadOnlyCollection<CompetencyCandidate> candidates, IReadOnlyCollection<CompetencyScore> scores, HashSet<string> allowedEvidenceIds)
    {
        var validKeys = scores.Where(score => score.EvidenceCount > 0).Select(score => score.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelItems = synthesis?.Items
            .Where(item => validKeys.Contains(item.CompetencyKey))
            .GroupBy(item => item.CompetencyKey, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(3)
            .Select(item =>
            {
                var deterministicScore = scores.First(score => score.Key.Equals(item.CompetencyKey, StringComparison.OrdinalIgnoreCase)).Score;
                var baseline = deterministicScore;
                var candidate = candidates.First(candidate => candidate.Rule.Key.Equals(item.CompetencyKey, StringComparison.OrdinalIgnoreCase));
                var evidence = SelectCompetencyEvidence(item.EvidenceIds, candidate.Reports.Select(report => report.Id), allowedEvidenceIds);
                var dueInDays = Math.Clamp(item.DueInDays, 7, 90);
                return new NormalizedItem(item.CompetencyKey, Truncate(item.Title, 140), baseline, baseline.HasValue ? Math.Clamp(Math.Max(item.TargetScore ?? baseline.Value + 5, baseline.Value + 5), 0, 100) : null, Truncate(item.Action, 500), Truncate(item.Measurement, 500), Truncate(item.Resource, 500), dueInDays, evidence,
                    NormalizeGuide(item.Guide, item.CompetencyKey, candidate.Reports.Length, dueInDays));
            })
            .ToArray() ?? [];
        if (modelItems.Length > 0) return modelItems;
        return candidates.Where(candidate => candidate.Reports.Length > 0)
            .OrderBy(candidate => scores.First(score => score.Key == candidate.Rule.Key).Score.HasValue ? 0 : 1)
            .ThenBy(candidate => scores.First(score => score.Key == candidate.Rule.Key).Score).Take(3)
            .Select(candidate =>
            {
                var score = scores.First(item => item.Key == candidate.Rule.Key).Score;
                return new NormalizedItem(candidate.Rule.Key, candidate.Rule.Title, score, score.HasValue ? Math.Min(100, score.Value + 15) : null, DefaultAction(candidate.Rule.Key), DefaultMeasurement(candidate.Rule.Key), "Role-play semanal e revisão de evidências com o gestor.", 30, candidate.Reports.Take(5).Select(report => report.Id.ToString()).ToArray(), NormalizeGuide(null, candidate.Rule.Key, candidate.Reports.Length, 30));
            }).ToArray();
    }

    private static IReadOnlyCollection<IndividualCoachStrength> NormalizeStrengths(IndividualCoachResult? synthesis, IReadOnlyCollection<CompetencyScore> scores)
    {
        var validKeys = scores.Where(score => score.EvidenceCount > 0 && score.Score.HasValue).Select(score => score.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strengths = synthesis?.Strengths.Where(item => validKeys.Contains(item.Key)).Take(3)
            .Select(item =>
            {
                var deterministicScore = scores.First(score => score.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)).Score;
                return item with { Score = deterministicScore!.Value, Title = Truncate(item.Title, 140), Summary = Truncate(item.Summary, 500) };
            }).ToArray() ?? [];
        if (strengths.Length > 0) return strengths;
        var best = scores.Where(item => item.EvidenceCount > 0 && item.Score.HasValue).OrderByDescending(item => item.Score).FirstOrDefault();
        if (best is null) return [];
        return [new(best.Key, best.Title, "Competência relativamente mais consistente dentro da amostra disponível; deve ser preservada durante o ciclo.", best.Score!.Value)];
    }

    internal static string[] SelectCompetencyEvidence(IEnumerable<string> proposedIds, IEnumerable<Guid> competencyReportIds, HashSet<string> allowedIds)
    {
        var competencyIds = competencyReportIds.Select(id => id.ToString()).Where(allowedIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var competencySet = competencyIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = proposedIds.Where(competencySet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        return selected.Length > 0 ? selected : competencyIds.Take(5).ToArray();
    }

    internal static IndividualDevelopmentGuide NormalizeGuide(IndividualDevelopmentGuide? guide, string key, int evidenceCount, int dueInDays)
    {
        var horizon = Math.Clamp(dueInDays, 7, 90);
        var action = DefaultAction(key);
        var measurement = DefaultMeasurement(key);
        string Text(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : Truncate(value, 500);
        var steps = guide?.Steps?.Where(step => !string.IsNullOrWhiteSpace(step.Instruction) && !string.IsNullOrWhiteSpace(step.Deliverable))
            .Take(4).Select(step => new IndividualDevelopmentStep(Text(step.Title, "Prática orientada"), Truncate(step.Instruction, 500), Truncate(step.Deliverable, 500), Math.Clamp(step.DueInDays, 1, horizon)))
            .OrderBy(step => step.DueInDays).ToArray() ?? [];
        if (steps.Length < 2) steps =
        [
            new("Entender os casos", "Revisar com o gestor até cinco evidências vinculadas a esta competência e identificar o comportamento a praticar.", "Casos revisados e um comportamento escolhido para desenvolver.", Math.Min(3, horizon)),
            new("Aplicar nos atendimentos", action, "Registrar a ação aplicada e o retorno observado nos casos acompanhados.", Math.Min(14, horizon)),
            new("Revisar e ajustar", measurement + " Comparar com a linha de base em uma revisão com o gestor.", "Revisão documentada com evolução observada, limitações e próximo compromisso.", horizon)
        ];
        return new(
            Text(guide?.Diagnosis, $"Esta competência possui {evidenceCount} relatório(s) de suporte. Revise os casos vinculados antes de concluir quais comportamentos precisam de ajuste."),
            Text(guide?.WhyItMatters, "A prática busca tornar o atendimento e os próximos passos mais consistentes. O efeito deve ser verificado no ciclo, sem presumir aumento de vendas."),
            Text(guide?.FirstAction, action),
            Text(guide?.ManagerSupport, "Reservar uma revisão semanal para analisar casos, demonstrar a prática e combinar o próximo passo com o colaborador."),
            Text(guide?.SuccessCriteria, measurement), steps);
    }

    private static async Task InsertAssessmentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, PlanRequest plan, ObjectiveMetrics metrics, int coverage, int confidence, IReadOnlyCollection<CompetencyScore> scores, IReadOnlyCollection<IndividualCoachStrength> strengths, IReadOnlyCollection<NormalizedItem> items, IReadOnlyCollection<string> limitations, string model, string fingerprint, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO skopos_coach_user_assessments
                (id, company_id, user_id, group_id, date_from, date_to, status, report_count, opportunity_count,
                 source_count, coverage_score, confidence, median_response_seconds, p90_response_seconds,
                 activity_count, completed_activity_count, overdue_activity_count, competency_scores_json,
                 strengths_json, focus_areas_json, limitations_json, model, prompt_fingerprint, completed_at, operational_context_json)
            VALUES (@id, @companyId, @userId, @groupId, @from, @to, 'completed', @reports, @opportunities,
                    @sources, @coverage, @confidence, @median, @p90, @activities, @completed, @overdue,
                    @scores::jsonb, @strengths::jsonb, @items::jsonb, @limitations::jsonb, @model, @fingerprint, now(), @operational::jsonb)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("companyId", plan.CompanyId); command.Parameters.AddWithValue("userId", plan.UserId);
        command.Parameters.Add("groupId", NpgsqlDbType.Uuid).Value = plan.GroupId is null ? DBNull.Value : plan.GroupId.Value; command.Parameters.AddWithValue("from", plan.From); command.Parameters.AddWithValue("to", plan.To);
        command.Parameters.AddWithValue("reports", metrics.ReportCount); command.Parameters.AddWithValue("opportunities", metrics.OpportunityCount); command.Parameters.AddWithValue("sources", metrics.SourceCount);
        command.Parameters.AddWithValue("coverage", coverage); command.Parameters.AddWithValue("confidence", confidence); command.Parameters.Add("median", NpgsqlDbType.Integer).Value = metrics.MedianResponseSeconds is null ? DBNull.Value : metrics.MedianResponseSeconds.Value;
        command.Parameters.Add("p90", NpgsqlDbType.Integer).Value = metrics.P90ResponseSeconds is null ? DBNull.Value : metrics.P90ResponseSeconds.Value; command.Parameters.AddWithValue("activities", metrics.ActivityCount);
        command.Parameters.AddWithValue("completed", metrics.CompletedActivityCount); command.Parameters.AddWithValue("overdue", metrics.OverdueActivityCount);
        command.Parameters.AddWithValue("operational", metrics.OperationalContext?.GetRawText() ?? "{}");
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
            var signal = candidate.Signals
                .Where(item => item.Report.Id == report.Id)
                .OrderByDescending(item => item.Reviewed)
                .ThenBy(item => item.Score)
                .FirstOrDefault();
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("assessment", assessmentId); command.Parameters.AddWithValue("report", report.Id); command.Parameters.AddWithValue("key", candidate.Rule.Key);
            command.Parameters.AddWithValue("relevance", signal is null ? Math.Min(85, 60 + candidate.Reports.Length * 3) : signal.Reviewed ? 100 : Math.Clamp(signal.Confidence, 1, 95));
            command.Parameters.AddWithValue("excerpt", Truncate(SkoposCoachProjectionService.SanitizeForCoach(signal?.Justification ?? report.Summary), 280));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdatePlanAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid planId, Guid assessmentId, string summary, string objective, int coverage, int confidence, IReadOnlyCollection<IndividualCoachStrength> strengths, IReadOnlyCollection<string> limitations, string model, string fingerprint, int horizonDays, IReadOnlyCollection<string> methodology, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE skopos_development_plans SET assessment_id = @assessment, status = 'draft', title = 'Plano de desenvolvimento individual',
                summary = @summary, objective = @objective, start_date = current_date, due_date = current_date + @horizonDays,
                coverage_score = @coverage, confidence = @confidence, strengths_json = @strengths::jsonb,
                limitations_json = @limitations::jsonb, methodology_json = @methodology::jsonb, model = @model, prompt_fingerprint = @fingerprint,
                error_message = null, updated_at = now() WHERE id = @id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("assessment", assessmentId); command.Parameters.AddWithValue("summary", summary); command.Parameters.AddWithValue("objective", objective);
        command.Parameters.AddWithValue("horizonDays", horizonDays);
        command.Parameters.AddWithValue("methodology", JsonSerializer.Serialize(methodology, JsonOptions));
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
            command.Parameters.Add("baseline", NpgsqlDbType.Integer).Value = item.BaselineScore is null ? DBNull.Value : item.BaselineScore.Value; command.Parameters.Add("target", NpgsqlDbType.Integer).Value = item.TargetScore is null ? DBNull.Value : item.TargetScore.Value; command.Parameters.AddWithValue("action", item.Action);
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
    private static int? Score(CompetencyCandidate candidate, ObjectiveMetrics metrics) => ScoreFromEvidence(
        candidate.Signals.Select(signal => new StructuredScoreInput(signal.Score, signal.Confidence, signal.Weight, signal.Reviewed)));
    internal static int? ScoreFromEvidence(IEnumerable<StructuredScoreInput> values)
    {
        var covered = values.Where(value => value.Confidence > 0 && value.Weight > 0).ToArray();
        return covered.Length == 0 ? null : ScoreStructured(covered);
    }
    internal static int ScoreStructured(IEnumerable<StructuredScoreInput> values)
    {
        var signals = values.Where(value => value.Confidence > 0 && value.Weight > 0).ToArray();
        if (signals.Length == 0) return 0;
        var denominator = signals.Sum(value => (double)value.Weight * value.Confidence / 100d * (value.Reviewed ? 2d : 1d));
        if (denominator <= 0) return 0;
        var numerator = signals.Sum(value => Math.Clamp(value.Score, 0, 100) * (double)value.Weight * value.Confidence / 100d * (value.Reviewed ? 2d : 1d));
        return Math.Clamp((int)Math.Round(numerator / denominator), 0, 100);
    }
    internal static int Coverage(ObjectiveMetrics metrics) => Math.Min(100, Math.Min(metrics.ReportCount, 10) * 4 + Math.Min(metrics.OpportunityCount, 3) * 10 + Math.Min(metrics.SourceCount, 2) * 10 + (metrics.ResponseCount > 0 ? 10 : 0));
    internal static int Confidence(ObjectiveMetrics metrics) => Math.Min(95, 35 + Math.Min(metrics.ReportCount, 10) * 3 + Math.Min(metrics.OpportunityCount, 3) * 5 + Math.Min(metrics.SourceCount, 2) * 5 + (metrics.ResponseCount > 0 ? 5 : 0));
    private static string Truncate(string? value, int length) { var normalized = value?.Trim() ?? ""; return normalized.Length <= length ? normalized : normalized[..length]; }
    private static string DefaultAction(string key) => key switch { "cadence" => "Registrar o próximo passo ao final de cada atendimento e executar os retornos no prazo combinado.", "qualification" => "Aplicar um roteiro curto de necessidade, urgência, autoridade e critério de decisão antes de avançar.", "objections" => "Praticar escuta, validação e resposta baseada em valor para as objeções recorrentes da amostra.", "proposal" => "Apresentar proposta conectando valor, condição e próximo compromisso verificável.", "product" => "Revisar os produtos mais presentes na carteira e praticar sua conexão com necessidades reais.", "service" => "Reduzir esperas e tornar cada resposta clara, contextual e orientada ao próximo passo.", _ => "Revisar a carteira diariamente e concluir atividades comerciais não automáticas dentro do prazo." };
    private static string DefaultMeasurement(string key) => key switch { "cadence" => "Ao menos 90% dos atendimentos avaliados com próximo passo registrado.", "qualification" => "Quatro critérios de qualificação registrados em 80% das oportunidades novas.", "objections" => "Registrar objeção, resposta aplicada e reação do cliente em cinco casos revisados.", "proposal" => "Toda proposta enviada com data de retorno e compromisso seguinte definidos.", "service" => "Reduzir a mediana e o P90 de resposta em relação à linha de base do período.", _ => "Revisão semanal de cinco evidências com evolução observável no indicador da competência." };

    internal sealed record ObjectiveMetrics(int ReportCount, int OpportunityCount, int SourceCount, int ResponseCount, int? MedianResponseSeconds, int? P90ResponseSeconds, int ActivityCount, int CompletedActivityCount, int OverdueActivityCount, JsonElement? OperationalContext = null);
    private sealed record PlanRequest(Guid Id, Guid CompanyId, Guid UserId, Guid? GroupId, DateOnly From, DateOnly To);
    private sealed record Report(Guid Id, string SourceType, Guid? OpportunityId, string Summary, DateTime OccurredAt);
    private sealed record CompetencyRule(string Key, string Title, string[] Keywords);
    private sealed record CompetencyCandidate(CompetencyRule Rule, Report[] Reports, ScorecardSignal[] Signals);
    private sealed record CompetencyScore(string Key, string Title, int? Score, int EvidenceCount, string ScoreOrigin);
    private sealed record NormalizedItem(string CompetencyKey, string Title, int? BaselineScore, int? TargetScore, string Action, string Measurement, string Resource, int DueInDays, IReadOnlyCollection<string> EvidenceIds, IndividualDevelopmentGuide Guide);
    private sealed record ScorecardSignal(Report Report, string CriterionKey, string CriterionTitle, decimal Weight, int Score, int Confidence, bool Reviewed, string Justification, string? CompetencyKey, Guid TemplateKey, int TemplateVersion);
    internal sealed record StructuredScoreInput(int Score, int Confidence, decimal Weight, bool Reviewed);
    private sealed record CriterionMapping(string CompetencyKey, string[] Aliases);
}
