using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresAnalysisResultStore(NpgsqlDataSource dataSource) : IAnalysisResultStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveRiskAnalysisAsync(OpportunityAnalysisContext context, RiskAnalysisResult result, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!string.Equals(context.Opportunity.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            await using var clearRisk = new NpgsqlCommand("""
                update opportunities
                set risk = false
                where id = @opportunityId
                  and risk = true
                """, connection);
            clearRisk.Parameters.AddWithValue("opportunityId", Guid.Parse(context.Opportunity.Id));
            await clearRisk.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string insertSql = """
            insert into ai_insights (id, opportunity_id, title, message, kind, confidence, status, company_id, created_at, updated_at)
            values (@id, @opportunityId, @title, @message, @kind, @confidence, @status, @companyId, @createdAt, @updatedAt)
            """;

        var now = DateTime.UtcNow;
        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("opportunityId", Guid.Parse(context.Opportunity.Id));
            command.Parameters.AddWithValue("title", $"Risk analysis: {ToDatabaseValue(result.RiskLevel)}");
            command.Parameters.AddWithValue("message", JsonSerializer.Serialize(new
            {
                riskLevel = ToDatabaseValue(result.RiskLevel),
                riskScore = result.RiskScore,
                reasons = result.Reasons,
                recommendations = result.Recommendations,
                snapshot = result.SnapshotUpdate,
                triggerEvent = context.TriggerEvent.Type
            }, SerializerOptions));
            command.Parameters.AddWithValue("kind", "risk-analysis");
            command.Parameters.AddWithValue("confidence", NpgsqlDbType.Numeric, result.RiskScore / 100m);
            command.Parameters.AddWithValue("status", "active");
            command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = string.IsNullOrWhiteSpace(context.Opportunity.CompanyId) ? DBNull.Value : Guid.Parse(context.Opportunity.CompanyId);
            command.Parameters.AddWithValue("createdAt", now);
            command.Parameters.AddWithValue("updatedAt", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateOpportunitySql = """
            update opportunities
            set risk = @risk, updated_at = @updatedAt
            where id = @opportunityId
              and status = 'active'
              and risk is distinct from @risk
            """;

        await using (var command = new NpgsqlCommand(updateOpportunitySql, connection, transaction))
        {
            command.Parameters.AddWithValue("risk", result.RiskLevel == RiskLevel.High);
            command.Parameters.AddWithValue("updatedAt", now);
            command.Parameters.AddWithValue("opportunityId", Guid.Parse(context.Opportunity.Id));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpsertDailySnapshotAsync(connection, transaction, context, result.SnapshotUpdate, cancellationToken);
        await UpsertPrimaryRecommendationSuggestionAsync(connection, transaction, context, result, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertPrimaryRecommendationSuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OpportunityAnalysisContext context,
        RiskAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var recommendation = result.Recommendations.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        var contact = context.Contacts.FirstOrDefault(contact => Guid.TryParse(contact.Id, out _));
        if (string.IsNullOrWhiteSpace(recommendation)
            || contact is null
            || !Guid.TryParse(context.Opportunity.Id, out var opportunityId)
            || !Guid.TryParse(context.Opportunity.CompanyId, out var companyId)
            || !Guid.TryParse(contact.Id, out var contactId))
        {
            return;
        }

        var recommendationKey = CreateRecommendationKey(recommendation);
        var payload = JsonSerializer.Serialize(new
        {
            activityType = "follow-up",
            channel = ResolveRecommendationChannel(context.TriggerEvent.Type),
            notes = recommendation,
            opportunityId = context.Opportunity.Id,
            recommendationKey,
            source = "risk-analysis"
        }, SerializerOptions);

        // A risk analysis is recalculated after every relevant event. Serialize per opportunity so
        // repeated runs update the current recommendation instead of creating a noisy backlog.
        await using (var lockCommand = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", $"risk-analysis-suggestion:{companyId}:{opportunityId}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            with recently_accepted as (
                select id
                from ai_agent_suggestions
                where company_id = @companyId
                  and agent_key = 'risk-analysis'
                  and suggestion_type = 'activity'
                  and status = 'accepted'
                  and resolved_at >= now() - interval '30 days'
                  and payload ->> 'recommendationKey' = @recommendationKey
            ), pending as (
                select id
                from ai_agent_suggestions
                where company_id = @companyId
                  and agent_key = 'risk-analysis'
                  and suggestion_type = 'activity'
                  and status = 'pending'
                  and payload ->> 'opportunityId' = @opportunityIdText
                order by updated_at desc
                limit 1
            ), updated as (
                update ai_agent_suggestions suggestion
                set contact_id = @contactId,
                    title = @title,
                    description = @description,
                    payload = @payload,
                    confidence_score = @confidenceScore,
                    generation_reasons = @generationReasons,
                    verification_status = 'pending',
                    verification_attempt_count = 0,
                    next_verification_at = null,
                    last_verified_at = null,
                    priority_at = null,
                    priority_notified_at = null,
                    evidence_fingerprint = null,
                    verification_confidence = null,
                    verification_reason = null,
                    verification_model = null,
                    verification_evidence = '[]'::jsonb,
                    updated_at = now()
                where suggestion.id = (select id from pending)
                  and not exists (select 1 from recently_accepted)
                returning suggestion.id
            )
            insert into ai_agent_suggestions (
                id, company_id, agent_key, suggestion_type, status, contact_id,
                title, description, payload, confidence_score, generation_reasons, created_at, updated_at)
            select
                @id, @companyId, 'risk-analysis', 'activity', 'pending', @contactId,
                @title, @description, @payload, @confidenceScore, @generationReasons, now(), now()
            where not exists (select 1 from updated)
              and not exists (select 1 from recently_accepted);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("contactId", contactId);
        command.Parameters.AddWithValue("opportunityIdText", opportunityId.ToString());
        command.Parameters.AddWithValue("title", "Executar próximo passo recomendado");
        command.Parameters.AddWithValue("description", Truncate(recommendation, 3000));
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(result.RiskScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(result.Reasons, SerializerOptions);
        command.Parameters.AddWithValue("recommendationKey", recommendationKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ResolveRecommendationChannel(string eventType)
    {
        if (eventType.Contains("whatsapp", StringComparison.OrdinalIgnoreCase)) return "whatsapp";
        if (eventType.Contains("instagram", StringComparison.OrdinalIgnoreCase)) return "instagram";
        if (eventType.Contains("linkedin", StringComparison.OrdinalIgnoreCase)) return "linkedin";
        if (eventType.Contains("email", StringComparison.OrdinalIgnoreCase)) return "email";
        if (eventType.Contains("meeting", StringComparison.OrdinalIgnoreCase)) return "meeting";
        return "call";
    }

    private static string CreateRecommendationKey(string recommendation)
    {
        var normalized = string.Join(' ', recommendation
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static async Task UpsertDailySnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OpportunityAnalysisContext context,
        OpportunityAnalysisSnapshotUpdate snapshot,
        CancellationToken cancellationToken)
    {
        var snapshotAt = snapshot.SnapshotAt.ToUniversalTime();
        var dayStart = DateTime.SpecifyKind(snapshotAt.Date, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);

        const string updateSql = """
            update opportunity_analysis_snapshots
            set stage_id = @stageId,
                days_in_stage = @daysInStage,
                activities_open = @activitiesOpen,
                activities_overdue = @activitiesOverdue,
                last_interaction_days = @lastInteractionDays,
                last_interaction_at = @lastInteractionAt,
                health_score = @healthScore,
                confidence_score = @confidenceScore
            where opportunity_id = @opportunityId
              and company_id = @companyId
              and snapshot_source = 'daily'
              and snapshot_at >= @dayStart
              and snapshot_at < @dayEnd
            """;

        await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddSnapshotParameters(command, context, snapshot, dayStart, dayEnd);
            var updatedRows = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updatedRows > 0)
            {
                return;
            }
        }

        const string insertSql = """
            insert into opportunity_analysis_snapshots (
                id,
                opportunity_id,
                stage_id,
                snapshot_source,
                snapshot_at,
                days_in_stage,
                activities_open,
                activities_overdue,
                last_interaction_days,
                last_interaction_at,
                health_score,
                confidence_score,
                company_id,
                created_at)
            values (
                @id,
                @opportunityId,
                @stageId,
                'daily',
                @snapshotAt,
                @daysInStage,
                @activitiesOpen,
                @activitiesOverdue,
                @lastInteractionDays,
                @lastInteractionAt,
                @healthScore,
                @confidenceScore,
                @companyId,
                @createdAt)
            """;

        await using (var command = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddSnapshotParameters(command, context, snapshot, dayStart, dayEnd);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("snapshotAt", snapshotAt);
            command.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddSnapshotParameters(
        NpgsqlCommand command,
        OpportunityAnalysisContext context,
        OpportunityAnalysisSnapshotUpdate snapshot,
        DateTime dayStart,
        DateTime dayEnd)
    {
        command.Parameters.AddWithValue("opportunityId", Guid.Parse(context.Opportunity.Id));
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value =
            string.IsNullOrWhiteSpace(context.Opportunity.CompanyId)
                ? DBNull.Value
                : Guid.Parse(context.Opportunity.CompanyId);
        command.Parameters.AddWithValue("stageId", Guid.Parse(context.Opportunity.StageId));
        command.Parameters.AddWithValue("daysInStage", snapshot.DaysInStage);
        command.Parameters.AddWithValue("activitiesOpen", snapshot.ActivitiesOpen);
        command.Parameters.AddWithValue("activitiesOverdue", snapshot.ActivitiesOverdue);
        command.Parameters.AddWithValue("lastInteractionDays", snapshot.LastInteractionDays);
        command.Parameters.AddWithValue(
            "lastInteractionAt",
            NpgsqlDbType.TimestampTz,
            snapshot.LastInteractionAt is null ? DBNull.Value : snapshot.LastInteractionAt.Value.ToUniversalTime());
        command.Parameters.AddWithValue("healthScore", snapshot.HealthScore);
        command.Parameters.AddWithValue("confidenceScore", snapshot.ConfidenceScore);
        command.Parameters.AddWithValue("dayStart", dayStart);
        command.Parameters.AddWithValue("dayEnd", dayEnd);
    }

    private static string ToDatabaseValue(RiskLevel level)
        => level switch
        {
            RiskLevel.High => "HIGH",
            RiskLevel.Medium => "MEDIUM",
            _ => "LOW"
        };
}
