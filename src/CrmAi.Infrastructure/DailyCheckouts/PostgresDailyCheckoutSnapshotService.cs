using System.Text.Json;
using System.Text.Json.Nodes;
using CrmAi.Application;
using CrmAi.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.DailyCheckouts;

public sealed class PostgresDailyCheckoutSnapshotService(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    IOpenAiDailyCheckoutClient openAiClient,
    ILogger<PostgresDailyCheckoutSnapshotService> logger) : IDailyCheckoutSnapshotService
{
    private const string AgentKey = "daily-checkout";
    private const int ScheduledBackfillDays = 30;
    private const int MaxSnapshotsPerCompanyPerCycle = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task GenerateDueSnapshotsAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var settings = await ReadCompanySettingsAsync(connection, cancellationToken);

        foreach (var setting in settings)
        {
            try
            {
                var timeZone = ResolveTimeZone(setting.TimeZoneId);
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone);
                var runAt = ParseRunAt(setting.RunAt);
                var latestDueDate = ResolveLatestDueTargetDate(
                    localNow,
                    runAt,
                    setting.ConsiderPreviousDayWhenRunBeforeNoon);
                var generatedCount = 0;

                for (var dayOffset = 0;
                     dayOffset < ScheduledBackfillDays && generatedCount < MaxSnapshotsPerCompanyPerCycle;
                     dayOffset++)
                {
                    var targetDate = latestDueDate.AddDays(-dayOffset);
                    var runStartedAtUtc = ResolveRunStartedAtUtc(
                        targetDate,
                        runAt,
                        setting.ConsiderPreviousDayWhenRunBeforeNoon,
                        timeZone);
                    var lockKey = $"daily-checkout:{setting.CompanyId}:{targetDate:yyyy-MM-dd}";
                    if (!await TryAcquireRunLockAsync(connection, lockKey, cancellationToken))
                    {
                        continue;
                    }

                    try
                    {
                        var agentSettings = await settingsRepository.GetAsync(AgentKey, setting.CompanyId, cancellationToken);
                        if (await SnapshotAlreadyGeneratedForRunAsync(
                                connection,
                                setting.CompanyId,
                                targetDate,
                                runStartedAtUtc,
                                RequiresOpenAiAnalysis(agentSettings),
                                cancellationToken))
                        {
                            continue;
                        }

                        await GenerateSnapshotAsync(connection, setting, targetDate, timeZone, agentSettings, cancellationToken);
                        generatedCount++;
                    }
                    finally
                    {
                        await ReleaseRunLockAsync(connection, lockKey, CancellationToken.None);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "Failed to generate scheduled daily checkout snapshots for company {CompanyId}.",
                    setting.CompanyId);
            }
        }
    }

    public async Task GenerateSnapshotAsync(string companyId, DateOnly? date, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(companyId, out _))
        {
            throw new ArgumentException("CompanyId must be a valid UUID.", nameof(companyId));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var setting = await ReadCompanySettingAsync(connection, companyId, cancellationToken);
        var timeZone = ResolveTimeZone(setting.TimeZoneId);
        var targetDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone).Date);
        var agentSettings = await settingsRepository.GetAsync(AgentKey, setting.CompanyId, cancellationToken);
        await GenerateSnapshotAsync(connection, setting, targetDate, timeZone, agentSettings, cancellationToken);
    }

    private async Task GenerateSnapshotAsync(
        NpgsqlConnection connection,
        DailyCheckoutSettingsSnapshot setting,
        DateOnly date,
        TimeZoneInfo timeZone,
        AiAgentRuntimeSettings agentSettings,
        CancellationToken cancellationToken)
    {
        var dayStartUtc = ToUtc(date.ToDateTime(TimeOnly.MinValue), timeZone);
        var dayEndUtc = ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone);
        var context = await BuildContextAsync(connection, setting, date, timeZone, dayStartUtc, dayEndUtc, cancellationToken);
        var ai = await AnalyzeBestEffortAsync(setting, agentSettings, FilterContext(context, agentSettings.ContextEntityKeys), cancellationToken);
        var payload = BuildPayload(date, setting, context, ai);
        await UpsertSnapshotAsync(connection, setting.CompanyId, date, payload, cancellationToken);
    }

    internal static DateOnly ResolveTargetDate(DateTime localNow, TimeSpan runAt, bool considerPreviousDayWhenRunBeforeNoon)
    {
        var usePreviousDay = considerPreviousDayWhenRunBeforeNoon && runAt < TimeSpan.FromHours(12);
        return DateOnly.FromDateTime(usePreviousDay ? localNow.Date.AddDays(-1) : localNow.Date);
    }

    internal static DateOnly ResolveLatestDueTargetDate(DateTime localNow, TimeSpan runAt, bool considerPreviousDayWhenRunBeforeNoon)
    {
        var scheduledDate = localNow.TimeOfDay >= runAt ? localNow.Date : localNow.Date.AddDays(-1);
        var usePreviousDay = considerPreviousDayWhenRunBeforeNoon && runAt < TimeSpan.FromHours(12);
        return DateOnly.FromDateTime(usePreviousDay ? scheduledDate.AddDays(-1) : scheduledDate);
    }

    private static DateTime ResolveRunStartedAtUtc(
        DateOnly targetDate,
        TimeSpan runAt,
        bool considerPreviousDayWhenRunBeforeNoon,
        TimeZoneInfo timeZone)
    {
        var useFollowingMorning = considerPreviousDayWhenRunBeforeNoon && runAt < TimeSpan.FromHours(12);
        var scheduledDate = useFollowingMorning ? targetDate.AddDays(1) : targetDate;
        return ToUtc(scheduledDate.ToDateTime(TimeOnly.FromTimeSpan(runAt)), timeZone);
    }

    internal static bool RequiresOpenAiAnalysis(AiAgentRuntimeSettings settings) =>
        settings.IsActive && !string.IsNullOrWhiteSpace(settings.ApiKey);

    private async Task<OpenAiDailyCheckoutResponse?> AnalyzeBestEffortAsync(DailyCheckoutSettingsSnapshot setting, AiAgentRuntimeSettings agentSettings, DailyCheckoutAnalysisInput input, CancellationToken cancellationToken)
    {
        try
        {
            if (!agentSettings.IsActive)
            {
                return null;
            }

            return await openAiClient.AnalyzeAsync(agentSettings, input, new AiAgentInvocationContext(
                PlatformArea: "daily-checkout",
                CompanyId: setting.CompanyId,
                ContextEntityKeys: agentSettings.ContextEntityKeys,
                Metadata: new Dictionary<string, object?>
                {
                    ["snapshotDate"] = input.Date.ToString("yyyy-MM-dd"),
                    ["runAt"] = setting.RunAt
                }), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Daily checkout AI analysis failed. Snapshot will use deterministic fallback.");
            return null;
        }
    }

    internal static DailyCheckoutAnalysisInput FilterContext(DailyCheckoutAnalysisInput input, IReadOnlyCollection<string> keys)
    {
        var enabled = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tables = JsonSerializer.SerializeToNode(input.Tables, SerializerOptions) as JsonObject ?? [];
        if (!enabled.Contains("opportunities"))
        {
            foreach (var name in new[] { "opened", "won", "lost", "focus", "movements" }) tables.Remove(name);
        }
        if (!enabled.Contains("users"))
        {
            tables.Remove("performance");
            tables.Remove("lowEffectiveness");
        }
        if (!enabled.Contains("daily_metrics")) tables.Remove("checkoutMetrics");
        if (!enabled.Contains("groups") && tables["filters"] is JsonObject filters) filters.Remove("groups");

        var charts = JsonSerializer.SerializeToNode(input.Charts, SerializerOptions) as JsonObject ?? [];
        if (!enabled.Contains("activities")) charts.Remove("activityChannels");
        if (!enabled.Contains("opportunities"))
        {
            charts.Remove("opportunityPulse");
            charts.Remove("riskMap");
            charts.Remove("pipelineMovement");
            charts.Remove("opportunityOrigins");
        }
        if (!enabled.Contains("contacts")) charts.Remove("contactOrigins");

        return input with
        {
            Totals = enabled.Contains("daily_metrics") ? input.Totals : new { },
            Metrics = enabled.Contains("daily_metrics") ? input.Metrics : [],
            Charts = charts,
            Tables = tables,
            UpdatedOpportunities = enabled.Contains("opportunities") ? input.UpdatedOpportunities : [],
            RiskItems = enabled.Contains("opportunities") ? input.RiskItems : [],
            LowEffectiveness = enabled.Contains("users") ? input.LowEffectiveness : []
        };
    }

    private static object BuildPayload(DateOnly date, DailyCheckoutSettingsSnapshot setting, DailyCheckoutAnalysisInput context, OpenAiDailyCheckoutResponse? ai)
    {
        var totals = context.Totals;
        return new
        {
            date,
            updatedAt = DateTime.UtcNow,
            generatedAt = DateTime.UtcNow,
            settings = new
            {
                setting.RunAt,
                setting.TimeZoneId,
                setting.ConsiderPreviousDayWhenRunBeforeNoon
            },
            executiveSummary = new
            {
                headline = Clean(ai?.ExecutiveSummary.Headline) ?? "Fechamento operacional gerado com os principais indicadores do dia.",
                focus = Clean(ai?.ExecutiveSummary.Focus) ?? "Priorizar oportunidades ativas com maior risco, valor e dias sem contato.",
                generatedBy = ai is null ? "deterministic" : "openai"
            },
            totals,
            context.Metrics,
            filters = new { groups = Array.Empty<string>(), users = Array.Empty<object>(), statuses = new[] { "active", "won", "lost" }, tags = Array.Empty<string>() },
            context.Charts,
            context.Tables,
            alerts = ai?.Alerts ?? Array.Empty<DailyCheckoutTextItemResponse>(),
            recommendations = ai?.Recommendations ?? Array.Empty<DailyCheckoutRecommendationResponse>()
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<DailyCheckoutAnalysisInput> BuildContextAsync(
        NpgsqlConnection connection,
        DailyCheckoutSettingsSnapshot setting,
        DateOnly date,
        TimeZoneInfo timeZone,
        DateTime dayStartUtc,
        DateTime dayEndUtc,
        CancellationToken cancellationToken)
    {
        var monthStartUtc = ToUtc(new DateTime(date.Year, date.Month, 1), timeZone);
        var monthEndUtc = dayEndUtc;
        var totals = await ReadOneAsync(connection, """
            with latest_risk as (
                select distinct on (i.opportunity_id)
                    i.opportunity_id,
                    upper(replace(i.title, 'Risk analysis: ', '')) as risk_level,
                    round(i.confidence * 100)::int as risk_score,
                    coalesce(((regexp_match(i.message, '"healthScore":([0-9]+)'))[1])::int, 0) as health_score,
                    coalesce(((regexp_match(i.message, '"lastInteractionDays":([0-9]+)'))[1])::int, 0) as last_interaction_days,
                    coalesce(((regexp_match(i.message, '"activitiesOverdue":([0-9]+)'))[1])::int, 0) as activities_overdue
                from ai_insights i
                where i.company_id = @companyId
                  and i.kind = 'risk-analysis'
                  and i.created_at < @endsAt
                order by i.opportunity_id, i.created_at desc
            ),
            latest_transition as (
                select distinct on (h.opportunity_id)
                    h.opportunity_id,
                    h.to_status
                from opportunity_history h
                where h.company_id = @companyId
                  and h.event_type = 'status_transition'
                  and h.created_at < @endsAt
                order by h.opportunity_id, h.created_at desc
            ),
            opportunity_state as (
                select
                    o.*,
                    coalesce(transition.to_status, 'active') as status_at_end
                from opportunities o
                left join latest_transition transition on transition.opportunity_id = o.id
                where o.company_id = @companyId
                  and o.created_at < @endsAt
            ),
            outcome_events as (
                select distinct on (h.opportunity_id, h.to_status)
                    h.opportunity_id,
                    h.to_status
                from opportunity_history h
                where h.company_id = @companyId
                  and h.event_type = 'status_transition'
                  and h.created_at >= @startsAt
                  and h.created_at < @endsAt
                  and h.to_status in ('won', 'lost')
                order by h.opportunity_id, h.to_status, h.created_at
            ),
            movement_events as (
                select
                    h.opportunity_id,
                    source_stage.sort_order as from_position,
                    target_stage.sort_order as to_position
                from opportunity_history h
                left join pipeline_stages source_stage
                  on source_stage.id = ((regexp_match(h.event, 'fromStageId=([0-9a-fA-F-]{36})'))[1])::uuid
                left join pipeline_stages target_stage on target_stage.id = h.stage_id
                where h.company_id = @companyId
                  and h.event_type = 'stage_transition'
                  and h.created_at >= @startsAt
                  and h.created_at < @endsAt
            ),
            advanced_opportunities as (
                select distinct opportunity_id
                from movement_events
                where to_position > from_position
            ),
            movement_totals as (
                select
                    count(distinct opportunity_id)::int as moved_today,
                    count(distinct opportunity_id) filter (where to_position > from_position)::int as advanced_today,
                    count(distinct opportunity_id) filter (where to_position < from_position)::int as regressed_today
                from movement_events
            ),
            advanced_value as (
                select coalesce(sum(o.value), 0)::numeric as potential_advanced
                from advanced_opportunities a
                join opportunity_state o on o.id = a.opportunity_id
                where o.status_at_end = 'active'
            )
            select
                count(*) filter (where o.status_at_end = 'active')::int as activeOpportunities,
                count(*) filter (where o.created_at >= @startsAt and o.created_at < @endsAt)::int as openedToday,
                (
                    select count(*)::int
                    from contacts c
                    where c.company_id = @companyId
                      and c.created_at >= @startsAt
                      and c.created_at < @endsAt
                ) as newContacts,
                (select count(*)::int from outcome_events where to_status = 'won') as wonToday,
                (select count(*)::int from outcome_events where to_status = 'lost') as lostToday,
                (select moved_today from movement_totals) as movedToday,
                (select advanced_today from movement_totals) as advancedToday,
                (select regressed_today from movement_totals) as regressedToday,
                (select potential_advanced from advanced_value) as movedValue,
                coalesce(avg(lr.health_score) filter (where o.status_at_end = 'active' and lr.opportunity_id is not null), 0)::numeric as averageQuality,
                coalesce(avg(lr.risk_score) filter (where o.status_at_end = 'active' and lr.opportunity_id is not null), 0)::numeric as averageRisk,
                count(*) filter (where o.status_at_end = 'active' and lr.risk_level = 'HIGH')::int as criticalAlerts,
                count(*) filter (where o.status_at_end = 'active' and lr.risk_level = 'MEDIUM')::int as mediumRisk,
                count(*) filter (where o.status_at_end = 'active' and lr.opportunity_id is not null)::int as analyzedRisk
            from opportunity_state o
            left join latest_risk lr on lr.opportunity_id = o.id
            """, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);

        var activityChannels = await ReadRowsAsync(connection, """
            select lower(channel) as label, count(*)::int as value
            from activities
            where company_id = @companyId
              and status = 'done'
              and date_at >= @startsAt
              and date_at < @endsAt
            group by lower(channel)
            order by value desc
            """, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);

        var opportunityOrigins = await ReadRowsAsync(connection, """
            select coalesce(oo.name, 'Sem origem') as label, count(*)::int as value
            from opportunities o
            left join opportunity_origins oo on oo.id = o.origin_id
            where o.company_id = @companyId
              and o.created_at >= @startsAt
              and o.created_at < @endsAt
            group by coalesce(oo.name, 'Sem origem')
            order by value desc, label
            """, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);

        var contactOrigins = await ReadRowsAsync(connection, """
            select coalesce(co.name, nullif(btrim(c.origin), ''), 'Sem origem') as label, count(*)::int as value
            from contacts c
            left join contact_origins co on co.id = c.origin_id
            where c.company_id = @companyId
              and c.created_at >= @startsAt
              and c.created_at < @endsAt
            group by coalesce(co.name, nullif(btrim(c.origin), ''), 'Sem origem')
            order by value desc, label
            """, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);

        var performanceDetails = await ReadRowsAsync(connection, """
            with active_users as (
                select id, name, role, group_id
                from users
                where company_id = @companyId and is_active = true
            ),
            applicable_metrics as (
                select
                    u.id as user_id,
                    m.id,
                    m.name,
                    m.period,
                    m.target,
                    m.unit,
                    m.activity_channel,
                    m.pipeline_id,
                    m.stage_id,
                    @startsAt as starts_at
                from active_users u
                join daily_checkin_metrics m
                    on m.company_id = @companyId
                   and m.is_active = true
                   and m.period = 'daily'
                   and m.created_at < @endsAt
                   and (m.group_id is null or m.group_id = u.group_id)
                   and (m.user_id is null or m.user_id = u.id)
            ),
            metric_results as (
                select
                    m.user_id,
                    m.id,
                    m.name,
                    m.period,
                    m.target,
                    m.unit,
                    m.activity_channel,
                    coalesce(result.actual, 0)::int as actual
                from applicable_metrics m
                left join lateral (
                    select count(*)::int as actual
                    from (
                        select 1
                        from activities a
                        where m.unit = 'activity'
                          and a.company_id = @companyId
                          and a.owner_user_id = m.user_id
                          and a.status = 'done'
                          and a.date_at >= m.starts_at and a.date_at < @endsAt
                          and (m.activity_channel is null or lower(a.channel) = lower(m.activity_channel))
                        union all
                        select 1
                        from opportunities o
                        where m.unit = 'opportunity'
                          and o.company_id = @companyId
                          and o.owner_user_id = m.user_id
                          and o.created_at >= m.starts_at and o.created_at < @endsAt
                          and (m.pipeline_id is null or o.pipeline_id = m.pipeline_id)
                          and (m.stage_id is null or o.stage_id = m.stage_id)
                        union all
                        select 1
                        from opportunity_history h
                        where m.unit = 'opportunity_won'
                          and h.company_id = @companyId
                          and h.user_id = m.user_id
                          and h.event_type = 'status_transition'
                          and h.to_status = 'won'
                          and h.created_at >= m.starts_at and h.created_at < @endsAt
                          and (m.pipeline_id is null or h.pipeline_id = m.pipeline_id)
                          and (m.stage_id is null or h.stage_id = m.stage_id)
                        union all
                        select 1
                        from (
                            select distinct h.opportunity_id
                            from opportunity_history h
                            where m.unit = 'opportunity_updated'
                              and h.company_id = @companyId
                              and h.user_id = m.user_id
                              and h.event_type = 'stage_transition'
                              and h.created_at >= m.starts_at and h.created_at < @endsAt
                              and (m.pipeline_id is null or h.pipeline_id = m.pipeline_id)
                              and (m.stage_id is null or h.stage_id = m.stage_id)
                        ) moved
                        union all
                        select 1
                        from notes n
                        where m.unit = 'note'
                          and n.company_id = @companyId
                          and n.author_user_id = m.user_id
                          and n.created_at >= m.starts_at and n.created_at < @endsAt
                    ) facts
                ) result on true
            )
            select
                u.id::text as id,
                u.name,
                coalesce(g.name, u.role, 'Sem grupo') as "group",
                mr.id::text as "goalId",
                mr.name as "goalName",
                mr.period as "goalPeriod",
                mr.unit as "goalUnit",
                mr.activity_channel as "goalActivityChannel",
                coalesce(mr.target, 0)::int as "goalTarget",
                coalesce(mr.actual, 0)::int as "goalActual"
            from active_users u
            left join user_groups g on g.id = u.group_id
            left join metric_results mr on mr.user_id = u.id
            order by u.name, mr.name
            """, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);
        var performance = BuildPerformanceRows(performanceDetails);

        var checkoutMetrics = await ReadCheckoutMetricRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, monthStartUtc, monthEndUtc, cancellationToken);
        var opened = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, "o.created_at >= @startsAt and o.created_at < @endsAt", "o.created_at desc", cancellationToken);
        var won = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, """
            exists (
                select 1 from opportunity_history outcome
                where outcome.opportunity_id = o.id
                  and outcome.company_id = @companyId
                  and outcome.event_type = 'status_transition'
                  and outcome.to_status = 'won'
                  and outcome.created_at >= @startsAt
                  and outcome.created_at < @endsAt)
            """, "event_at desc", cancellationToken);
        var lost = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, """
            exists (
                select 1 from opportunity_history outcome
                where outcome.opportunity_id = o.id
                  and outcome.company_id = @companyId
                  and outcome.event_type = 'status_transition'
                  and outcome.to_status = 'lost'
                  and outcome.created_at >= @startsAt
                  and outcome.created_at < @endsAt)
            """, "event_at desc", cancellationToken);
        var focus = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, """
            o.status_at_end = 'active'
            and (
                lr.risk_level in ('HIGH', 'MEDIUM')
                or coalesce(lr.activities_overdue, 0) > 0
                or coalesce(lr.last_interaction_days, 0) >= 5
                or (lr.opportunity_id is null and o.risk = true))
            """, """
            case lr.risk_level when 'HIGH' then 3 when 'MEDIUM' then 2 else 1 end desc,
            lr.risk_score desc,
            coalesce(lr.activities_overdue, 0) desc,
            coalesce(lr.last_interaction_days, 0) desc,
            o.value desc
            """, cancellationToken);
        var movements = await ReadMovementRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);
        var updated = movements.Cast<object>().ToArray();

        var lowEffectiveness = performance
            .Where(row =>
                Convert.ToInt32(row.GetValueOrDefault("planned") ?? 0) > 0 &&
                Convert.ToDecimal(row.GetValueOrDefault("percent") ?? 0m) < 60m)
            .Take(10)
            .Cast<object>()
            .ToArray();
        var totalPlanned = performance.Sum(row => Convert.ToInt32(row.GetValueOrDefault("planned") ?? 0));
        var totalExecuted = performance.Sum(row => Convert.ToInt32(row.GetValueOrDefault("executed") ?? 0));
        var totalCredited = performance.Sum(row => Convert.ToInt32(row.GetValueOrDefault("credited") ?? 0));
        var goalPercent = totalPlanned == 0 ? 0 : Math.Round(totalCredited / (decimal)totalPlanned * 100, 1);
        var checkoutPlanned = checkoutMetrics.Sum(row => Convert.ToInt32(row.GetValueOrDefault("target") ?? 0));
        var checkoutExecuted = checkoutMetrics.Sum(row => Convert.ToInt32(row.GetValueOrDefault("actual") ?? 0));
        var checkoutCredited = checkoutMetrics.Sum(row => Math.Min(
            Convert.ToInt32(row.GetValueOrDefault("actual") ?? 0),
            Convert.ToInt32(row.GetValueOrDefault("target") ?? 0)));
        var checkoutGoalPercent = checkoutPlanned == 0 ? 0 : Math.Round(checkoutCredited / (decimal)checkoutPlanned * 100, 1);
        var averageRisk = Convert.ToDecimal(totals.GetValueOrDefault("averageRisk") ?? 0m);

        var metrics = new object[]
        {
            new { key = "goalPercent", title = "Meta individual do check-in", value = goalPercent, suffix = "%", description = $"{totalExecuted} executado / {totalPlanned} planejado" },
            new { key = "checkoutGoalPercent", title = "Metas operacionais do checkout", value = checkoutGoalPercent, suffix = "%", description = $"{checkoutExecuted} realizado / {checkoutPlanned} planejado" },
            new { key = "newContacts", title = "Novos contatos", value = totals.GetValueOrDefault("newContacts") ?? 0, description = "Contatos cadastrados no CRM no dia selecionado" },
            new { key = "contactsDone", title = "Interacoes concluidas", value = activityChannels.Sum(row => Convert.ToInt32(row.GetValueOrDefault("value") ?? 0)), description = "Atividades concluidas e registradas por canal no dia" },
            new { key = "movedOpportunities", title = "Oportunidades que avancaram", value = totals.GetValueOrDefault("advancedToday") ?? 0, description = $"{totals.GetValueOrDefault("movedToday") ?? 0} mudaram de etapa; {totals.GetValueOrDefault("regressedToday") ?? 0} regrediram" },
            new { key = "movedValue", title = "Valor potencial que avancou", value = totals.GetValueOrDefault("movedValue") ?? 0, prefix = "R$", description = "Valor atual, nao receita, das oportunidades ativas que avancaram de etapa" },
            new { key = "criticalAlerts", title = "Alertas criticos", value = totals.GetValueOrDefault("criticalAlerts") ?? 0, description = "Riscos para amanha cedo" },
            new { key = "averageRisk", title = "Score medio de risco", value = Math.Round(averageRisk, 1), suffix = "/100", description = $"Quanto maior, maior a atencao; {totals.GetValueOrDefault("analyzedRisk") ?? 0} ativas analisadas" },
            new { key = "openedToday", title = "Abertas do dia", value = totals.GetValueOrDefault("openedToday") ?? 0, description = "Oportunidades criadas no dia selecionado" },
            new { key = "wonToday", title = "Ganhas do dia", value = totals.GetValueOrDefault("wonToday") ?? 0, description = "Oportunidades ganhas no dia selecionado" },
            new { key = "lostToday", title = "Perdidas do dia", value = totals.GetValueOrDefault("lostToday") ?? 0, description = "Oportunidades marcadas como perda" },
            new { key = "topTomorrow", title = "Top 10 para amanha", value = focus.Count, description = "Lista pronta de ataque" }
        };

        var tables = new
        {
            opened,
            won,
            lost,
            focus,
            movements,
            performance,
            checkoutMetrics,
            lowEffectiveness,
            filters = new
            {
                groups = performance.Select(row => row.GetValueOrDefault("group")?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray(),
                users = performance.Select(row => new { id = row.GetValueOrDefault("id")?.ToString(), name = row.GetValueOrDefault("name")?.ToString() }).ToArray(),
                statuses = new[] { "active", "won", "lost" },
                tags = Array.Empty<string>()
            }
        };

        var charts = new
        {
            activityChannels,
            opportunityOrigins,
            contactOrigins,
            opportunityPulse = new[]
            {
                new { label = "Abertas do dia", value = totals.GetValueOrDefault("openedToday") ?? 0 },
                new { label = "Ganhas do dia", value = totals.GetValueOrDefault("wonToday") ?? 0 },
                new { label = "Perdidas do dia", value = totals.GetValueOrDefault("lostToday") ?? 0 }
            },
            riskMap = new[]
            {
                new { label = "Risco medio", value = totals.GetValueOrDefault("mediumRisk") ?? 0 },
                new { label = "Risco critico", value = totals.GetValueOrDefault("criticalAlerts") ?? 0 }
            },
            pipelineMovement = new[]
            {
                new { label = "Avancos", value = totals.GetValueOrDefault("advancedToday") ?? 0 },
                new { label = "Regressoes", value = totals.GetValueOrDefault("regressedToday") ?? 0 }
            }
        };

        return new DailyCheckoutAnalysisInput(date, setting, totals, metrics, charts, tables, updated.Cast<object>().Take(30).ToArray(), focus.Cast<object>().Take(30).ToArray(), lowEffectiveness);
    }

    private static List<Dictionary<string, object?>> BuildPerformanceRows(IReadOnlyCollection<Dictionary<string, object?>> details) =>
        details
            .GroupBy(row => row.GetValueOrDefault("id")?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var goals = group
                    .Where(row => !string.IsNullOrWhiteSpace(row.GetValueOrDefault("goalId")?.ToString()))
                    .Select(row =>
                    {
                        var target = Convert.ToInt32(row.GetValueOrDefault("goalTarget") ?? 0);
                        var actual = Convert.ToInt32(row.GetValueOrDefault("goalActual") ?? 0);
                        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["id"] = row.GetValueOrDefault("goalId")?.ToString(),
                            ["name"] = row.GetValueOrDefault("goalName")?.ToString() ?? "Metrica",
                            ["period"] = row.GetValueOrDefault("goalPeriod")?.ToString() ?? "daily",
                            ["unit"] = row.GetValueOrDefault("goalUnit")?.ToString() ?? "activity",
                            ["activityChannel"] = row.GetValueOrDefault("goalActivityChannel")?.ToString(),
                            ["target"] = target,
                            ["actual"] = actual,
                            ["percent"] = target == 0 ? 0m : Math.Round(Math.Min(actual, target) / (decimal)target * 100, 1)
                        };
                    })
                    .ToArray();
                var planned = goals.Sum(goal => Convert.ToInt32(goal["target"] ?? 0));
                var executed = goals.Sum(goal => Convert.ToInt32(goal["actual"] ?? 0));
                var credited = goals.Sum(goal => Math.Min(Convert.ToInt32(goal["actual"] ?? 0), Convert.ToInt32(goal["target"] ?? 0)));
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = first.GetValueOrDefault("id")?.ToString(),
                    ["name"] = first.GetValueOrDefault("name")?.ToString(),
                    ["group"] = first.GetValueOrDefault("group")?.ToString(),
                    ["planned"] = planned,
                    ["executed"] = executed,
                    ["credited"] = credited,
                    ["percent"] = planned == 0 ? 0m : Math.Round(credited / (decimal)planned * 100, 1),
                    ["goals"] = goals
                };
            })
            .OrderByDescending(row => Convert.ToDecimal(row["percent"] ?? 0m))
            .ThenBy(row => row["name"]?.ToString())
            .ToList();

    private static async Task<List<Dictionary<string, object?>>> ReadCheckoutMetricRowsAsync(
        NpgsqlConnection connection,
        string? companyId,
        DateTime startsAt,
        DateTime endsAt,
        DateTime monthStartsAt,
        DateTime monthEndsAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with metrics as (
                select
                    m.id,
                    m.name,
                    m.period,
                    m.target,
                    m.unit,
                    m.group_id,
                    g.name as group_name,
                    m.activity_channel,
                    case when m.period = 'monthly' then @monthStartsAt else @startsAt end as starts_at,
                    @endsAt as ends_at
                from daily_checkout_metrics m
                left join user_groups g on g.id = m.group_id
                where m.company_id = @companyId
                  and m.is_active = true
                  and m.created_at < @endsAt
                order by m.sort_order, m.name
            )
            select
                m.id::text as id,
                m.name,
                m.period,
                m.unit,
                m.group_id::text as "groupId",
                m.group_name as "groupName",
                m.target,
                coalesce(results.actual, 0)::int as actual,
                case when m.target = 0 then 0 else least(100, round(coalesce(results.actual, 0)::numeric / m.target * 100, 1)) end as percent
            from metrics m
            left join lateral (
                select count(*)::int as actual
                from (
                    select 1
                    from activities a
                    left join users u on u.id = a.owner_user_id
                    where m.unit = 'activity'
                      and a.company_id = @companyId
                      and a.status = 'done'
                      and a.date_at >= m.starts_at
                      and a.date_at < m.ends_at
                      and (m.activity_channel is null or lower(a.channel) = lower(m.activity_channel))
                      and (m.group_id is null or u.group_id = m.group_id)
                    union all
                    select 1
                    from opportunities o
                    left join users u on u.id = o.owner_user_id
                    where m.unit = 'opportunity'
                      and o.company_id = @companyId
                      and o.created_at >= m.starts_at
                      and o.created_at < m.ends_at
                      and (m.group_id is null or u.group_id = m.group_id)
                    union all
                    select 1
                    from opportunity_history h
                    left join users u on u.id = h.user_id
                    where m.unit = 'opportunity_won'
                      and h.company_id = @companyId
                      and h.event_type = 'status_transition'
                      and h.to_status = 'won'
                      and h.created_at >= m.starts_at
                      and h.created_at < m.ends_at
                      and (m.group_id is null or u.group_id = m.group_id)
                    union all
                    select 1
                    from (
                        select distinct h.opportunity_id, h.user_id
                        from opportunity_history h
                        where h.company_id = @companyId
                          and h.event_type = 'stage_transition'
                          and h.created_at >= m.starts_at
                          and h.created_at < m.ends_at
                    ) moved
                    left join users u on u.id = moved.user_id
                    where m.unit = 'opportunity_updated'
                      and (m.group_id is null or u.group_id = m.group_id)
                    union all
                    select 1
                    from notes n
                    left join users u on u.id = n.author_user_id
                    where m.unit = 'note'
                      and n.company_id = @companyId
                      and n.created_at >= m.starts_at
                      and n.created_at < m.ends_at
                      and (m.group_id is null or u.group_id = m.group_id)
                ) facts
            ) results on true
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddCompanyParameter(command, companyId);
        command.Parameters.AddWithValue("startsAt", NpgsqlDbType.TimestampTz, startsAt);
        command.Parameters.AddWithValue("endsAt", NpgsqlDbType.TimestampTz, endsAt);
        command.Parameters.AddWithValue("monthStartsAt", NpgsqlDbType.TimestampTz, monthStartsAt);
        command.Parameters.AddWithValue("monthEndsAt", NpgsqlDbType.TimestampTz, monthEndsAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : NormalizeValue(reader.GetValue(index));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadOpportunityRowsAsync(
        NpgsqlConnection connection,
        string? companyId,
        DateTime startsAt,
        DateTime endsAt,
        string condition,
        string orderBy,
        CancellationToken cancellationToken)
    {
        var sql = $$"""
            with latest_risk as (
                select distinct on (i.opportunity_id)
                    i.opportunity_id,
                    upper(replace(i.title, 'Risk analysis: ', '')) as risk_level,
                    round(i.confidence * 100)::int as risk_score,
                    coalesce(((regexp_match(i.message, '"healthScore":([0-9]+)'))[1])::int, 0) as health_score,
                    coalesce(((regexp_match(i.message, '"confidenceScore":([0-9]+)'))[1])::int, 0) as confidence_score,
                    coalesce(((regexp_match(i.message, '"lastInteractionDays":([0-9]+)'))[1])::int, 0) as last_interaction_days,
                    coalesce(((regexp_match(i.message, '"activitiesOverdue":([0-9]+)'))[1])::int, 0) as activities_overdue,
                    i.message as risk_payload,
                    i.created_at as risk_analyzed_at
                from ai_insights i
                where i.company_id = @companyId
                  and i.kind = 'risk-analysis'
                  and i.created_at < @endsAt
                order by i.opportunity_id, i.created_at desc
            ),
            latest_transition as (
                select distinct on (h.opportunity_id)
                    h.opportunity_id,
                    h.to_status
                from opportunity_history h
                where h.company_id = @companyId
                  and h.event_type = 'status_transition'
                  and h.created_at < @endsAt
                order by h.opportunity_id, h.created_at desc
            ),
            opportunity_rows as (
                select
                    opportunity.*,
                    coalesce(transition.to_status, 'active') as status_at_end
                from opportunities opportunity
                left join latest_transition transition on transition.opportunity_id = opportunity.id
                where opportunity.company_id = @companyId
                  and opportunity.created_at < @endsAt
            )
            select
                o.id::text as id,
                o.name,
                o.status_at_end as status,
                o.value,
                o.created_at as "createdAt",
                o.updated_at as "updatedAt",
                (
                    select max(outcome.created_at)
                    from opportunity_history outcome
                    where outcome.opportunity_id = o.id
                      and outcome.company_id = @companyId
                      and outcome.event_type = 'status_transition'
                      and outcome.created_at >= @startsAt
                      and outcome.created_at < @endsAt
                ) as event_at,
                ps.title as stage,
                u.name as owner,
                coalesce(oo.name, 'Sem origem') as origin,
                coalesce(lr.health_score, 0)::int as "qualityScore",
                coalesce(lr.confidence_score, 0)::int as "confidenceScore",
                coalesce(lr.last_interaction_days, 0)::int as "daysWithoutContact",
                coalesce(lr.activities_overdue, 0)::int as "overdueActivities",
                coalesce(lr.risk_level, 'NOT_ANALYZED') as "riskLevel",
                coalesce(lr.risk_score, 0)::int as "riskScore",
                lr.risk_payload as "riskPayload",
                lr.risk_analyzed_at as "riskAnalyzedAt"
            from opportunity_rows o
            left join pipeline_stages ps on ps.id = o.stage_id
            left join users u on u.id = o.owner_user_id
            left join opportunity_origins oo on oo.id = o.origin_id
            left join latest_risk lr on lr.opportunity_id = o.id
            where {{condition}}
            order by {{orderBy}}
            limit 50
            """;

        return await ReadRowsAsync(connection, sql, companyId, startsAt, endsAt, cancellationToken);
    }

    private static async Task<List<Dictionary<string, object?>>> ReadMovementRowsAsync(
        NpgsqlConnection connection,
        string? companyId,
        DateTime startsAt,
        DateTime endsAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with latest_transition as (
                select distinct on (history.opportunity_id)
                    history.opportunity_id,
                    history.to_status
                from opportunity_history history
                where history.company_id = @companyId
                  and history.event_type = 'status_transition'
                  and history.created_at < @endsAt
                order by history.opportunity_id, history.created_at desc
            )
            select
                h.opportunity_id::text as id,
                o.name,
                o.value,
                coalesce(transition.to_status, 'active') as status,
                u.name as owner,
                source_stage.title as "fromStage",
                target_stage.title as "toStage",
                case
                    when target_stage.sort_order > source_stage.sort_order then 'advance'
                    when target_stage.sort_order < source_stage.sort_order then 'regression'
                    else 'lateral'
                end as direction,
                h.created_at as "eventAt"
            from opportunity_history h
            join opportunities o on o.id = h.opportunity_id
            left join latest_transition transition on transition.opportunity_id = o.id
            left join users u on u.id = h.user_id
            left join pipeline_stages source_stage
              on source_stage.id = ((regexp_match(h.event, 'fromStageId=([0-9a-fA-F-]{36})'))[1])::uuid
            left join pipeline_stages target_stage on target_stage.id = h.stage_id
            where h.company_id = @companyId
              and h.event_type = 'stage_transition'
              and h.created_at >= @startsAt
              and h.created_at < @endsAt
            order by h.created_at desc
            limit 50
            """;

        return await ReadRowsAsync(connection, sql, companyId, startsAt, endsAt, cancellationToken);
    }

    private static async Task<IReadOnlyCollection<DailyCheckoutSettingsSnapshot>> ReadCompanySettingsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                c.id as company_id,
                coalesce(s.run_at, '18:00') as run_at,
                coalesce(s.time_zone_id, 'America/Sao_Paulo') as time_zone_id,
                coalesce(s.consider_previous_day_when_run_before_noon, false) as consider_previous_day_when_run_before_noon
            from companies c
            left join daily_checkout_settings s on s.company_id = c.id
            where c.status = 'active'
            order by c.name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DailyCheckoutSettingsSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DailyCheckoutSettingsSnapshot(
                reader.GetGuid(reader.GetOrdinal("company_id")).ToString(),
                reader.GetString(reader.GetOrdinal("run_at")),
                reader.GetString(reader.GetOrdinal("time_zone_id")),
                reader.GetBoolean(reader.GetOrdinal("consider_previous_day_when_run_before_noon"))));
        }

        return rows;
    }

    private static async Task<DailyCheckoutSettingsSnapshot> ReadCompanySettingAsync(NpgsqlConnection connection, string companyId, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                c.id as company_id,
                coalesce(s.run_at, '18:00') as run_at,
                coalesce(s.time_zone_id, 'America/Sao_Paulo') as time_zone_id,
                coalesce(s.consider_previous_day_when_run_before_noon, false) as consider_previous_day_when_run_before_noon
            from companies c
            left join daily_checkout_settings s on s.company_id = c.id
            where c.id = @companyId
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = Guid.Parse(companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Company '{companyId}' was not found.");
        }

        return new DailyCheckoutSettingsSnapshot(
            reader.GetGuid(reader.GetOrdinal("company_id")).ToString(),
            reader.GetString(reader.GetOrdinal("run_at")),
            reader.GetString(reader.GetOrdinal("time_zone_id")),
            reader.GetBoolean(reader.GetOrdinal("consider_previous_day_when_run_before_noon")));
    }

    private static async Task<bool> SnapshotAlreadyGeneratedForRunAsync(
        NpgsqlConnection connection,
        string? companyId,
        DateOnly date,
        DateTime runStartedAtUtc,
        bool requireOpenAiAnalysis,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select 1
            from daily_checkout_snapshots
            where company_id = @companyId
              and snapshot_date = @snapshotDate
              and snapshot_at >= @runStartedAtUtc
              and (
                  not @requireOpenAiAnalysis
                  or payload_json #>> '{executiveSummary,generatedBy}' = 'openai'
              )
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddCompanyParameter(command, companyId);
        command.Parameters.AddWithValue("snapshotDate", date);
        command.Parameters.AddWithValue("runStartedAtUtc", NpgsqlDbType.TimestampTz, runStartedAtUtc);
        command.Parameters.AddWithValue("requireOpenAiAnalysis", requireOpenAiAnalysis);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> TryAcquireRunLockAsync(NpgsqlConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_try_advisory_lock(hashtextextended(@key, 0));", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task ReleaseRunLockAsync(NpgsqlConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended(@key, 0));", connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSnapshotAsync(NpgsqlConnection connection, string? companyId, DateOnly date, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions)
            .Replace("\\u0000", string.Empty, StringComparison.OrdinalIgnoreCase);
        const string sql = """
            insert into daily_checkout_snapshots (id, snapshot_date, snapshot_at, payload_json, company_id, created_at, updated_at)
            values (@id, @snapshotDate, @snapshotAt, @payload::jsonb, @companyId, @snapshotAt, @snapshotAt)
            on conflict (company_id, snapshot_date)
            do update set snapshot_at = excluded.snapshot_at,
                          payload_json = excluded.payload_json,
                          updated_at = excluded.updated_at
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("snapshotDate", date);
        command.Parameters.AddWithValue("snapshotAt", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        command.Parameters.AddWithValue("payload", json);
        AddCompanyParameter(command, companyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, object?>> ReadOneAsync(NpgsqlConnection connection, string sql, string? companyId, DateTime startsAt, DateTime endsAt, CancellationToken cancellationToken) =>
        (await ReadRowsAsync(connection, sql, companyId, startsAt, endsAt, cancellationToken)).FirstOrDefault() ?? [];

    private static async Task<List<Dictionary<string, object?>>> ReadRowsAsync(NpgsqlConnection connection, string sql, string? companyId, DateTime startsAt, DateTime endsAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        AddCompanyParameter(command, companyId);
        command.Parameters.AddWithValue("startsAt", NpgsqlDbType.TimestampTz, startsAt);
        command.Parameters.AddWithValue("endsAt", NpgsqlDbType.TimestampTz, endsAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : NormalizeValue(reader.GetValue(index));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object NormalizeValue(object value) => value switch
    {
        Guid guid => guid.ToString(),
        DateTime dateTime => dateTime.ToUniversalTime(),
        decimal decimalValue => decimalValue,
        _ => value
    };

    private static void AddCompanyParameter(NpgsqlCommand command, string? companyId) =>
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = string.IsNullOrWhiteSpace(companyId) ? DBNull.Value : Guid.Parse(companyId);

    private static TimeSpan ParseRunAt(string value) =>
        TimeSpan.TryParse(value, out var parsed) ? parsed : new TimeSpan(18, 0, 0);

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTime ToUtc(DateTime localDateTime, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified), timeZone);
}
