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
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task GenerateDueSnapshotsAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var settings = await ReadCompanySettingsAsync(connection, cancellationToken);

        foreach (var setting in settings)
        {
            var timeZone = ResolveTimeZone(setting.TimeZoneId);
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone);
            var runAt = ParseRunAt(setting.RunAt);
            if (localNow.TimeOfDay < runAt)
            {
                continue;
            }

            var targetDate = setting.ConsiderPreviousDayWhenRunBeforeNoon ? DateOnly.FromDateTime(localNow.Date.AddDays(-1)) : DateOnly.FromDateTime(localNow.Date);
            var runStartedAtUtc = ToUtc(targetDate.ToDateTime(TimeOnly.FromTimeSpan(runAt)), timeZone);
            var lockKey = $"daily-checkout:{setting.CompanyId}:{targetDate:yyyy-MM-dd}";
            if (!await TryAcquireRunLockAsync(connection, lockKey, cancellationToken))
            {
                continue;
            }

            try
            {
                if (await SnapshotAlreadyGeneratedForRunAsync(connection, setting.CompanyId, targetDate, runStartedAtUtc, cancellationToken))
                {
                    continue;
                }

                await GenerateSnapshotAsync(connection, setting, targetDate, timeZone, cancellationToken);
            }
            finally
            {
                await ReleaseRunLockAsync(connection, lockKey, CancellationToken.None);
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
        await GenerateSnapshotAsync(connection, setting, targetDate, timeZone, cancellationToken);
    }

    private async Task GenerateSnapshotAsync(NpgsqlConnection connection, DailyCheckoutSettingsSnapshot setting, DateOnly date, TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var dayStartUtc = ToUtc(date.ToDateTime(TimeOnly.MinValue), timeZone);
        var dayEndUtc = ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone);
        var context = await BuildContextAsync(connection, setting, date, timeZone, dayStartUtc, dayEndUtc, cancellationToken);
        var agentSettings = await settingsRepository.GetAsync(AgentKey, setting.CompanyId, cancellationToken);
        var ai = await AnalyzeBestEffortAsync(setting, agentSettings, FilterContext(context, agentSettings.ContextEntityKeys), cancellationToken);
        var payload = BuildPayload(date, setting, context, ai);
        await UpsertSnapshotAsync(connection, setting.CompanyId, date, payload, cancellationToken);
    }

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
            foreach (var name in new[] { "opened", "won", "lost", "focus" }) tables.Remove(name);
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
        }

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
        var monthEndUtc = ToUtc(new DateTime(date.Year, date.Month, 1).AddMonths(1), timeZone);
        var totals = await ReadOneAsync(connection, """
            with latest_scores as (
                select distinct on (opportunity_id)
                    opportunity_id,
                    health_score,
                    confidence_score,
                    last_interaction_days,
                    activities_overdue
                from opportunity_analysis_snapshots
                where company_id = @companyId
                order by opportunity_id, snapshot_at desc
            )
            select
                count(*) filter (where o.status = 'active')::int as activeOpportunities,
                count(*) filter (where o.created_at >= @startsAt and o.created_at < @endsAt)::int as openedToday,
                count(*) filter (where o.status = 'won' and o.updated_at >= @startsAt and o.updated_at < @endsAt)::int as wonToday,
                count(*) filter (where o.status = 'lost' and o.updated_at >= @startsAt and o.updated_at < @endsAt)::int as lostToday,
                count(*) filter (where o.updated_at >= @startsAt and o.updated_at < @endsAt and o.created_at < @startsAt)::int as movedToday,
                coalesce(sum(o.value) filter (where o.updated_at >= @startsAt and o.updated_at < @endsAt), 0)::numeric as movedValue,
                coalesce(avg(ls.health_score), 0)::numeric as averageQuality,
                count(*) filter (where o.status = 'active' and (o.risk = true or coalesce(ls.activities_overdue, 0) > 0 or coalesce(ls.last_interaction_days, 0) >= 15))::int as criticalAlerts,
                count(*) filter (where o.status = 'active' and coalesce(ls.last_interaction_days, 0) >= 7 and coalesce(ls.last_interaction_days, 0) < 15)::int as mediumRisk
            from opportunities o
            left join latest_scores ls on ls.opportunity_id = o.id
            where o.company_id = @companyId
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

        var performance = await ReadRowsAsync(connection, """
            with active_users as (
                select id, name, role, group_id
                from users
                where company_id = @companyId and is_active = true
            ),
            user_targets as (
                select
                    u.id as user_id,
                    coalesce(sum(m.target), 0)::int as planned
                from active_users u
                left join daily_checkin_metrics m
                    on m.company_id = @companyId
                   and m.is_active = true
                   and m.period = 'daily'
                   and (m.group_id is null or m.group_id = u.group_id)
                group by u.id
            ),
            executed as (
                select owner_user_id as user_id, count(*)::int as amount
                from activities
                where company_id = @companyId and status = 'done' and date_at >= @startsAt and date_at < @endsAt and owner_user_id is not null
                group by owner_user_id
                union all
                select owner_user_id as user_id, count(*)::int as amount
                from opportunities
                where company_id = @companyId and created_at >= @startsAt and created_at < @endsAt and owner_user_id is not null
                group by owner_user_id
                union all
                select author_user_id as user_id, count(*)::int as amount
                from notes
                where company_id = @companyId and created_at >= @startsAt and created_at < @endsAt and author_user_id is not null
                group by author_user_id
            ),
            user_execution as (
                select user_id, sum(amount)::int as executed
                from executed
                group by user_id
            )
            select
                u.id::text as id,
                u.name,
                coalesce(g.name, u.role, 'Sem grupo') as "group",
                ut.planned,
                coalesce(ue.executed, 0)::int as executed,
                case when ut.planned = 0 then 0 else round(coalesce(ue.executed, 0)::numeric / ut.planned * 100, 1) end as percent
            from active_users u
            join user_targets ut on ut.user_id = u.id
            left join user_groups g on g.id = u.group_id
            left join user_execution ue on ue.user_id = u.id
            order by percent desc, u.name
            """, setting.CompanyId, dayStartUtc, dayEndUtc, cancellationToken);

        var checkoutMetrics = await ReadCheckoutMetricRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, monthStartUtc, monthEndUtc, cancellationToken);
        var opened = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, "o.created_at >= @startsAt and o.created_at < @endsAt", "o.created_at desc", cancellationToken);
        var won = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, "o.status = 'won' and o.updated_at >= @startsAt and o.updated_at < @endsAt", "o.updated_at desc", cancellationToken);
        var lost = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, "o.status = 'lost' and o.updated_at >= @startsAt and o.updated_at < @endsAt", "o.updated_at desc", cancellationToken);
        var focus = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, "o.status = 'active' and (o.risk = true or coalesce(ls.last_interaction_days, 0) >= 5)", "o.risk desc, coalesce(ls.last_interaction_days, 0) desc, o.value desc", cancellationToken);
        var updated = await ReadOpportunityRowsAsync(connection, setting.CompanyId, dayStartUtc, dayEndUtc, "o.updated_at >= @startsAt and o.updated_at < @endsAt", "o.updated_at desc", cancellationToken);

        var lowEffectiveness = performance.Where(row => Convert.ToDecimal(row.GetValueOrDefault("percent") ?? 0m) < 60m).Take(10).Cast<object>().ToArray();
        var totalPlanned = performance.Sum(row => Convert.ToInt32(row.GetValueOrDefault("planned") ?? 0));
        var totalExecuted = performance.Sum(row => Convert.ToInt32(row.GetValueOrDefault("executed") ?? 0));
        var goalPercent = totalPlanned == 0 ? 0 : Math.Round(totalExecuted / (decimal)totalPlanned * 100, 1);
        var checkoutPlanned = checkoutMetrics.Sum(row => Convert.ToInt32(row.GetValueOrDefault("target") ?? 0));
        var checkoutExecuted = checkoutMetrics.Sum(row => Convert.ToInt32(row.GetValueOrDefault("actual") ?? 0));
        var checkoutGoalPercent = checkoutPlanned == 0 ? 0 : Math.Round(checkoutExecuted / (decimal)checkoutPlanned * 100, 1);
        var averageQuality = Convert.ToDecimal(totals.GetValueOrDefault("averageQuality") ?? 0m);

        var metrics = new object[]
        {
            new { key = "goalPercent", title = "Meta individual do check-in", value = goalPercent, suffix = "%", description = $"{totalExecuted} executado / {totalPlanned} planejado" },
            new { key = "checkoutGoalPercent", title = "Metas operacionais do checkout", value = checkoutGoalPercent, suffix = "%", description = $"{checkoutExecuted} realizado / {checkoutPlanned} planejado" },
            new { key = "contactsDone", title = "Contatos realizados", value = totalExecuted, description = "Atividades, notas e oportunidades do recorte" },
            new { key = "movedOpportunities", title = "Oportunidades movidas", value = totals.GetValueOrDefault("movedToday") ?? 0, description = "Atualizacoes reais do dia" },
            new { key = "movedValue", title = "Valor potencial movimentado", value = totals.GetValueOrDefault("movedValue") ?? 0, prefix = "R$", description = "Pipeline com atualizacao no recorte" },
            new { key = "criticalAlerts", title = "Alertas criticos", value = totals.GetValueOrDefault("criticalAlerts") ?? 0, description = "Riscos para amanha cedo" },
            new { key = "averageQuality", title = "Taxa media de qualidade", value = Math.Round(averageQuality, 1), suffix = "/100", description = "Execucao + avanco + CRM" },
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
            }
        };

        return new DailyCheckoutAnalysisInput(date, setting, totals, metrics, charts, tables, updated.Cast<object>().Take(30).ToArray(), focus.Cast<object>().Take(30).ToArray(), lowEffectiveness);
    }

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
                    case when m.period = 'monthly' then @monthEndsAt else @endsAt end as ends_at
                from daily_checkout_metrics m
                left join user_groups g on g.id = m.group_id
                where m.company_id = @companyId
                  and m.is_active = true
                order by m.sort_order, m.name
            )
            select
                m.id::text as id,
                m.name,
                m.period,
                m.unit,
                m.group_id::text as groupId,
                m.group_name as groupName,
                m.target,
                coalesce(results.actual, 0)::int as actual,
                case when m.target = 0 then 0 else round(coalesce(results.actual, 0)::numeric / m.target * 100, 1) end as percent
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
                    from opportunities o
                    left join users u on u.id = o.owner_user_id
                    where m.unit = 'opportunity_won'
                      and o.company_id = @companyId
                      and o.status = 'won'
                      and o.updated_at >= m.starts_at
                      and o.updated_at < m.ends_at
                      and (m.group_id is null or u.group_id = m.group_id)
                    union all
                    select 1
                    from opportunities o
                    left join users u on u.id = o.owner_user_id
                    where m.unit = 'opportunity_updated'
                      and o.company_id = @companyId
                      and o.updated_at >= m.starts_at
                      and o.updated_at < m.ends_at
                      and o.updated_at > o.created_at
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
            with latest_scores as (
                select distinct on (opportunity_id)
                    opportunity_id,
                    health_score,
                    confidence_score,
                    last_interaction_days,
                    activities_overdue
                from opportunity_analysis_snapshots
                where company_id = @companyId
                order by opportunity_id, snapshot_at desc
            )
            select
                o.id::text as id,
                o.name,
                o.status,
                o.value,
                o.created_at as createdAt,
                o.updated_at as updatedAt,
                ps.title as stage,
                u.name as owner,
                coalesce(ls.health_score, 0)::int as qualityScore,
                coalesce(ls.confidence_score, 0)::int as confidenceScore,
                coalesce(ls.last_interaction_days, 0)::int as daysWithoutContact,
                coalesce(ls.activities_overdue, 0)::int as overdueActivities
            from opportunities o
            left join pipeline_stages ps on ps.id = o.stage_id
            left join users u on u.id = o.owner_user_id
            left join latest_scores ls on ls.opportunity_id = o.id
            where o.company_id = @companyId
              and {{condition}}
            order by {{orderBy}}
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

    private static async Task<bool> SnapshotAlreadyGeneratedForRunAsync(NpgsqlConnection connection, string? companyId, DateOnly date, DateTime runStartedAtUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            select 1
            from daily_checkout_snapshots
            where company_id = @companyId
              and snapshot_date = @snapshotDate
              and snapshot_at >= @runStartedAtUtc
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddCompanyParameter(command, companyId);
        command.Parameters.AddWithValue("snapshotDate", date);
        command.Parameters.AddWithValue("runStartedAtUtc", NpgsqlDbType.TimestampTz, runStartedAtUtc);
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
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
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
