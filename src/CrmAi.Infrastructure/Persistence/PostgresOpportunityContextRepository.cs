using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresOpportunityContextRepository(NpgsqlDataSource dataSource, IAiAgentRuntimeSettingsRepository settingsRepository) : IOpportunityContextRepository
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

        var riskSettings = await settingsRepository.GetAsync("risk-analysis", opportunity.CompanyId, cancellationToken);
        var enabled = riskSettings.ContextEntityKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(triggerEvent.Type, "opportunity.whatsapp.conversation.batch", StringComparison.OrdinalIgnoreCase))
        {
            var whatsappSettings = await settingsRepository.GetAsync("whatsapp-conversation-analysis", opportunity.CompanyId, cancellationToken);
            enabled.UnionWith(whatsappSettings.ContextEntityKeys);
        }

        var stage = await ReadStageAsync(connection, Guid.Parse(opportunity.StageId), cancellationToken)
            ?? new PipelineStageSnapshot(opportunity.StageId, "Fase atual", 0);
        var notes = enabled.Contains("notes") ? await ReadNotesAsync(connection, opportunityId, cancellationToken) : [];
        var activities = enabled.Contains("activities") ? await ReadActivitiesAsync(connection, opportunityId, cancellationToken) : [];
        var contacts = enabled.Contains("contacts") ? await ReadContactsAsync(connection, opportunityId, cancellationToken) : [];
        var users = enabled.Contains("users") ? await ReadUsersAsync(connection, opportunityId, opportunity.OwnerUserId, cancellationToken) : [];
        var history = enabled.Contains("history") ? await ReadHistoryAsync(connection, opportunityId, cancellationToken) : [];
        var account = enabled.Contains("account") ? await ReadAccountAsync(connection, opportunityId, cancellationToken) : null;
        var products = enabled.Contains("products") ? await ReadProductsAsync(connection, opportunityId, cancellationToken) : [];
        var insights = enabled.Contains("agent_insights") ? await ReadAgentInsightsAsync(connection, opportunityId, cancellationToken) : [];
        var metricRules = enabled.Contains("commercial_rules") ? await ReadMetricRulesAsync(connection, opportunity.CompanyId, cancellationToken) : [];

        return new OpportunityAnalysisContext(opportunity, stage, notes, activities, contacts, users, history, account, products, insights, metricRules, triggerEvent);
    }

    private static async Task<OpportunitySnapshot?> ReadOpportunityAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select opportunity_id, company_id, name, pipeline_id, stage_id, account_id, owner_user_id, value, status, risk, created_at, updated_at, last_activity_at
            from vw_ai_agent_opportunity_context
            where opportunity_id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpportunitySnapshot(
            ReadGuid(reader, "opportunity_id"),
            ReadNullableGuid(reader, "company_id"),
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
            select stage_id, stage_title, stage_sort_order
            from vw_ai_agent_opportunity_context
            where stage_id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", stageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new PipelineStageSnapshot(ReadGuid(reader, "stage_id"), reader.GetString(reader.GetOrdinal("stage_title")), reader.GetInt32(reader.GetOrdinal("stage_sort_order")))
            : null;
    }

    private static async Task<IReadOnlyCollection<NoteSnapshot>> ReadNotesAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, text, author_user_id, created_at
            from vw_ai_agent_note_context
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
        var completionNotesExpression = await ResolveActivityCompletionNotesExpressionAsync(connection, cancellationToken);
        var sql = $"""
            select id, title, activity_type, channel, status, date_at, notes, {completionNotesExpression} as completion_notes, owner_user_id, created_at, updated_at
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
                ReadNullableString(reader, "completion_notes"),
                ReadNullableGuid(reader, "owner_user_id"),
                reader.GetDateTime(reader.GetOrdinal("created_at")),
                reader.GetDateTime(reader.GetOrdinal("updated_at"))));
        }

        return activities;
    }

    private static async Task<string> ResolveActivityCompletionNotesExpressionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            select column_name
            from information_schema.columns
            where table_schema = current_schema()
              and table_name = 'activities'
              and column_name in ('completion_notes', 'completed_notes')
            order by case column_name when 'completed_notes' then 0 else 1 end
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var column = await command.ExecuteScalarAsync(cancellationToken) as string;
        return column is "completed_notes" or "completion_notes" ? column : "null::text";
    }

    private static async Task<IReadOnlyCollection<ContactSnapshot>> ReadContactsAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, account_id, name, role, email, phone, owner_user_id, status
            from vw_ai_agent_contact_context
            where opportunity_id = @opportunityId
            order by name
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
            select id, name, role, is_active
            from vw_ai_agent_user_context
            where opportunity_id = @opportunityId
            order by name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
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
            from vw_ai_agent_history_context
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

    private static async Task<AccountSnapshot?> ReadAccountAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select account_id, account_name, account_segment, account_city, account_uf, account_status
            from vw_ai_agent_opportunity_context
            where opportunity_id = @opportunityId
              and account_id is not null
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new AccountSnapshot(
                ReadGuid(reader, "account_id"),
                reader.GetString(reader.GetOrdinal("account_name")),
                reader.GetString(reader.GetOrdinal("account_segment")),
                reader.GetString(reader.GetOrdinal("account_city")),
                reader.GetString(reader.GetOrdinal("account_uf")),
                reader.GetString(reader.GetOrdinal("account_status")))
            : null;
    }

    private static async Task<IReadOnlyCollection<ProductSnapshot>> ReadProductsAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, type, price, featured, status, summary
            from vw_ai_agent_product_context
            where opportunity_id = @opportunityId
            order by name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var products = new List<ProductSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            products.Add(new ProductSnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("type")),
                reader.GetDecimal(reader.GetOrdinal("price")),
                reader.GetBoolean(reader.GetOrdinal("featured")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("summary"))));
        }

        return products;
    }

    private static async Task<IReadOnlyCollection<AgentInsightSnapshot>> ReadAgentInsightsAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, title, message, kind, confidence, status, created_at, updated_at
            from vw_ai_agent_insight_context
            where opportunity_id = @opportunityId
              and kind <> 'risk'
            order by updated_at desc
            limit 20
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var insights = new List<AgentInsightSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            insights.Add(new AgentInsightSnapshot(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("title")),
                reader.GetString(reader.GetOrdinal("message")),
                reader.GetString(reader.GetOrdinal("kind")),
                ReadNullableDecimal(reader, "confidence"),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetDateTime(reader.GetOrdinal("created_at")),
                reader.GetDateTime(reader.GetOrdinal("updated_at"))));
        }

        return insights;
    }

    private static async Task<IReadOnlyCollection<CommercialAnalysisMetricRuleSnapshot>> ReadMetricRulesAsync(NpgsqlConnection connection, string? companyId, CancellationToken cancellationToken)
    {
        const string sql = """
            select r.id, r.metric_key, r.pipeline_id, r.stage_id, r.level, r.operator, r.threshold_value, r.threshold_unit
            from vw_ai_agent_commercial_metric_rule_context r
            where (@companyId::uuid is null or r.company_id = @companyId::uuid or r.company_id is null)
            order by r.metric_key, r.level
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = string.IsNullOrWhiteSpace(companyId) ? DBNull.Value : Guid.Parse(companyId);
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

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }
}
