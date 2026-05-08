using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresOpportunityContextRepository(NpgsqlDataSource dataSource) : IOpportunityContextRepository
{
    public async Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent triggerEvent, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(triggerEvent.OpportunityId, out var opportunityId))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var opportunity = await ReadOpportunityAsync(connection, opportunityId, cancellationToken);
        if (opportunity is null)
        {
            return null;
        }

        var stage = await ReadStageAsync(connection, Guid.Parse(opportunity.StageId), cancellationToken)
            ?? new PipelineStageSnapshot(opportunity.StageId, "Fase atual", 0);
        var notes = await ReadNotesAsync(connection, opportunityId, cancellationToken);
        var activities = await ReadActivitiesAsync(connection, opportunityId, cancellationToken);
        var contacts = await ReadContactsAsync(connection, opportunityId, cancellationToken);
        var users = await ReadUsersAsync(connection, opportunityId, opportunity.OwnerUserId, cancellationToken);
        var history = await ReadHistoryAsync(connection, opportunityId, cancellationToken);
        var metricRules = await ReadMetricRulesAsync(connection, cancellationToken);

        return new OpportunityAnalysisContext(opportunity, stage, notes, activities, contacts, users, history, metricRules, triggerEvent);
    }

    private static async Task<OpportunitySnapshot?> ReadOpportunityAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, pipeline_id, stage_id, account_id, owner_user_id, value, status, risk, created_at, updated_at, last_activity_at
            from opportunities
            where id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpportunitySnapshot(
            ReadGuid(reader, "id"),
            reader.GetString(reader.GetOrdinal("name")),
            ReadGuid(reader, "pipeline_id"),
            ReadGuid(reader, "stage_id"),
            ReadNullableGuid(reader, "account_id"),
            ReadNullableGuid(reader, "owner_user_id"),
            reader.GetDecimal(reader.GetOrdinal("value")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.GetBoolean(reader.GetOrdinal("risk")),
            reader.GetDateTime(reader.GetOrdinal("created_at")),
            reader.GetDateTime(reader.GetOrdinal("updated_at")),
            ReadNullableDateTime(reader, "last_activity_at"));
    }

    private static async Task<PipelineStageSnapshot?> ReadStageAsync(NpgsqlConnection connection, Guid stageId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, title, sort_order
            from pipeline_stages
            where id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", stageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new PipelineStageSnapshot(ReadGuid(reader, "id"), reader.GetString(reader.GetOrdinal("title")), reader.GetInt32(reader.GetOrdinal("sort_order")))
            : null;
    }

    private static async Task<IReadOnlyCollection<NoteSnapshot>> ReadNotesAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, text, author_user_id, created_at
            from notes
            where opportunity_id = @opportunityId
            order by created_at desc
            limit 50
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var notes = new List<NoteSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            notes.Add(new NoteSnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("text")),
                ReadNullableGuid(reader, "author_user_id"),
                reader.GetDateTime(reader.GetOrdinal("created_at"))));
        }

        return notes;
    }

    private static async Task<IReadOnlyCollection<ActivitySnapshot>> ReadActivitiesAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, title, activity_type, channel, status, date_at, notes, owner_user_id, created_at, updated_at
            from activities
            where opportunity_id = @opportunityId
            order by date_at desc
            limit 100
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var activities = new List<ActivitySnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            activities.Add(new ActivitySnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("title")),
                reader.GetString(reader.GetOrdinal("activity_type")),
                reader.GetString(reader.GetOrdinal("channel")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetDateTime(reader.GetOrdinal("date_at")),
                ReadNullableString(reader, "notes"),
                ReadNullableGuid(reader, "owner_user_id"),
                reader.GetDateTime(reader.GetOrdinal("created_at")),
                reader.GetDateTime(reader.GetOrdinal("updated_at"))));
        }

        return activities;
    }

    private static async Task<IReadOnlyCollection<ContactSnapshot>> ReadContactsAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select c.id, c.account_id, c.name, c.role, c.email, c.phone, c.owner_user_id, c.status
            from contacts c
            inner join opportunity_contacts oc on oc.contact_id = c.id
            where oc.opportunity_id = @opportunityId
            order by c.name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var contacts = new List<ContactSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            contacts.Add(new ContactSnapshot(
                ReadGuid(reader, "id"),
                ReadNullableGuid(reader, "account_id"),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("role")),
                reader.GetString(reader.GetOrdinal("email")),
                ReadNullableString(reader, "phone"),
                ReadNullableGuid(reader, "owner_user_id"),
                reader.GetString(reader.GetOrdinal("status"))));
        }

        return contacts;
    }

    private static async Task<IReadOnlyCollection<UserSnapshot>> ReadUsersAsync(NpgsqlConnection connection, Guid opportunityId, string? ownerUserId, CancellationToken cancellationToken)
    {
        const string sql = """
            select distinct u.id, u.name, u.role, u.is_active
            from users u
            where u.id in (
                select user_id from opportunity_users where opportunity_id = @opportunityId
                union
                select owner_user_id from activities where opportunity_id = @opportunityId and owner_user_id is not null
                union
                select author_user_id from notes where opportunity_id = @opportunityId and author_user_id is not null
                union
                select @ownerUserId::uuid where @ownerUserId is not null
            )
            order by u.name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("ownerUserId", string.IsNullOrWhiteSpace(ownerUserId) ? DBNull.Value : Guid.Parse(ownerUserId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var users = new List<UserSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new UserSnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("role")),
                reader.GetBoolean(reader.GetOrdinal("is_active"))));
        }

        return users;
    }

    private static async Task<IReadOnlyCollection<HistoryEventSnapshot>> ReadHistoryAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, event, user_id, created_at
            from opportunity_history
            where opportunity_id = @opportunityId
            order by created_at desc
            limit 100
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var events = new List<HistoryEventSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new HistoryEventSnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("event")),
                ReadNullableGuid(reader, "user_id"),
                reader.GetDateTime(reader.GetOrdinal("created_at"))));
        }

        return events;
    }

    private static async Task<IReadOnlyCollection<CommercialAnalysisMetricRuleSnapshot>> ReadMetricRulesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            select r.id, r.metric_key, r.pipeline_id, r.stage_id, r.level, r.operator, r.threshold_value, r.threshold_unit
            from commercial_analysis_metric_rules r
            inner join commercial_analysis_settings s on s.id = r.settings_id
            where s.is_active = true
              and s.id = (
                  select id
                  from commercial_analysis_settings
                  where is_active = true
                  order by updated_at desc
                  limit 1
              )
            order by r.metric_key, r.level
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rules = new List<CommercialAnalysisMetricRuleSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new CommercialAnalysisMetricRuleSnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("metric_key")),
                ReadNullableGuid(reader, "pipeline_id"),
                ReadNullableGuid(reader, "stage_id"),
                reader.GetString(reader.GetOrdinal("level")),
                reader.GetString(reader.GetOrdinal("operator")),
                reader.GetDecimal(reader.GetOrdinal("threshold_value")),
                reader.GetString(reader.GetOrdinal("threshold_unit"))));
        }

        return rules;
    }

    private static string ReadGuid(NpgsqlDataReader reader, string name)
        => reader.GetGuid(reader.GetOrdinal(name)).ToString();

    private static string? ReadNullableGuid(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal).ToString();
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? ReadNullableDateTime(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }
}
