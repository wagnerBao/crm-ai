using System.Text.Json;
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
            """;

        await using (var command = new NpgsqlCommand(updateOpportunitySql, connection, transaction))
        {
            command.Parameters.AddWithValue("risk", result.RiskLevel == RiskLevel.High);
            command.Parameters.AddWithValue("updatedAt", now);
            command.Parameters.AddWithValue("opportunityId", Guid.Parse(context.Opportunity.Id));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpsertDailySnapshotAsync(connection, transaction, context, result.SnapshotUpdate, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

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
