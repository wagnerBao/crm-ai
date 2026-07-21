using System.Text.Json;
using System.Text.Json.Serialization;
using CrmAi.Application;
using CrmAi.Domain;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresWhatsappConversationAnalysisScheduler(
    NpgsqlDataSource dataSource,
    IConfiguration configuration) : IWhatsappConversationAnalysisScheduler
{
    private const string Kind = "whatsapp-conversation-analysis";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private int DebounceMinutes => Math.Clamp(configuration.GetValue("WhatsappAnalysis:DebounceMinutes", 10), 1, 1440);

    public async Task ScheduleAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(opportunityEvent.OpportunityId, out var opportunityId))
        {
            return;
        }

        var conversationId = GetString(opportunityEvent, "conversationId");
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await IsAgentActiveAsync(connection, opportunityId, cancellationToken))
        {
            return;
        }

        var companyId = await ReadOpportunityCompanyIdAsync(connection, opportunityId, cancellationToken);
        var pending = await ReadPendingAsync(connection, opportunityId, conversationId, cancellationToken);
        var payload = pending?.Payload ?? new PendingWhatsappAnalysisPayload(
            ConversationId: conversationId,
            ContactId: GetString(opportunityEvent, "contactId"),
            FirstEventId: opportunityEvent.EventId,
            LatestEventId: opportunityEvent.EventId,
            MessageCount: 0,
            FirstMessageAt: opportunityEvent.OccurredAt,
            LatestMessageAt: opportunityEvent.OccurredAt,
            LatestText: GetString(opportunityEvent, "text"));

        payload = payload with
        {
            ContactId = FirstConfiguredValue(GetString(opportunityEvent, "contactId"), payload.ContactId),
            LatestEventId = opportunityEvent.EventId,
            MessageCount = payload.MessageCount + 1,
            LatestMessageAt = opportunityEvent.OccurredAt,
            LatestText = GetString(opportunityEvent, "text")
        };

        if (pending is null)
        {
            await InsertPendingAsync(connection, opportunityId, companyId, payload, cancellationToken);
            return;
        }

        await UpdatePendingAsync(connection, pending.Id, payload, cancellationToken);
    }

    public async Task<IReadOnlyCollection<OpportunityEvent>> ClaimDueAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var pending = await ReadDuePendingAsync(connection, Math.Clamp(limit, 1, 100), DebounceMinutes, cancellationToken);
        var events = new List<OpportunityEvent>(pending.Count);

        foreach (var item in pending)
        {
            var claimed = await ClaimAsync(connection, item.Id, cancellationToken);
            if (!claimed)
            {
                continue;
            }

            var processedUntil = await ReadLastProcessedMessageAtAsync(connection, item.OpportunityId, item.Payload.ConversationId, cancellationToken);
            var transcript = await ReadTranscriptAsync(connection, item.Payload.ConversationId, processedUntil, cancellationToken);
            var conversationSummary = await ReadConversationSummaryAsync(connection, item.Payload.ConversationId, cancellationToken);
            var agentSettings = await ReadWhatsappAgentSettingsAsync(connection, item.OpportunityId, DebounceMinutes, cancellationToken);
            var data = new Dictionary<string, object?>
            {
                ["conversationId"] = item.Payload.ConversationId,
                ["contactId"] = item.Payload.ContactId,
                ["firstEventId"] = item.Payload.FirstEventId,
                ["latestEventId"] = item.Payload.LatestEventId,
                ["messageCount"] = item.Payload.MessageCount,
                ["firstMessageAt"] = item.Payload.FirstMessageAt,
                ["latestMessageAt"] = item.Payload.LatestMessageAt,
                ["processedUntil"] = processedUntil,
                ["previousSummary"] = conversationSummary,
                ["additionalContext"] = agentSettings.Context,
                ["debounceMinutes"] = agentSettings.DebounceMinutes,
                ["text"] = string.IsNullOrWhiteSpace(transcript) ? item.Payload.LatestText : transcript
            };

            events.Add(new OpportunityEvent(
                item.Id.ToString(),
                "opportunity.whatsapp.conversation.batch",
                DateTime.UtcNow,
                item.OpportunityId.ToString(),
                null,
                data));
        }

        return events;
    }

    public async Task CompleteAsync(string eventId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(eventId, out var id))
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await UpdateStatusAsync(connection, id, "processed", null, cancellationToken);
    }

    public async Task FailAsync(string eventId, string error, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(eventId, out var id))
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await UpdateStatusAsync(connection, id, "failed", error, cancellationToken);
    }

    private static async Task<PendingWhatsappAnalysis?> ReadPendingAsync(NpgsqlConnection connection, Guid opportunityId, string conversationId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, opportunity_id, message
            from ai_insights
            where kind = @kind
              and status = 'pending'
              and opportunity_id = @opportunityId
              and message like @conversationToken
            order by updated_at desc
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("kind", Kind);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("conversationToken", $"%{conversationId}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPending(reader) : null;
    }

    private static async Task<IReadOnlyCollection<PendingWhatsappAnalysis>> ReadDuePendingAsync(NpgsqlConnection connection, int limit, int debounceMinutes, CancellationToken cancellationToken)
    {
        const string sql = """
            select insight.id, insight.opportunity_id, insight.message
            from ai_insights insight
            inner join opportunities opportunity on opportunity.id = insight.opportunity_id
            inner join lateral (
                select debounce_minutes, is_active
                from ai_agent_settings settings
                where settings.agent_key = 'whatsapp-conversation-analysis'
                  and (settings.company_id = opportunity.company_id or settings.company_id is null)
                order by case when settings.company_id = opportunity.company_id then 0 else 1 end, settings.updated_at desc
                limit 1
            ) settings on true
            where insight.kind = @kind
              and insight.status = 'pending'
              and settings.is_active = true
              and insight.updated_at <= now() - (settings.debounce_minutes * interval '1 minute')
            order by insight.updated_at
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("kind", Kind);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var result = new List<PendingWhatsappAnalysis>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadPending(reader));
        }

        return result;
    }

    private static async Task<bool> ClaimAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            update ai_insights
            set status = 'processing', updated_at = now()
            where id = @id and status = 'pending'
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertPendingAsync(NpgsqlConnection connection, Guid opportunityId, Guid? companyId, PendingWhatsappAnalysisPayload payload, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ai_insights (id, opportunity_id, title, message, kind, confidence, status, company_id, created_at, updated_at)
            values (@id, @opportunityId, 'Analise WhatsApp pendente', @message, @kind, null, 'pending', @companyId, now(), now())
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("message", JsonSerializer.Serialize(payload, SerializerOptions));
        command.Parameters.AddWithValue("kind", Kind);
        command.Parameters.AddWithValue("companyId", companyId is null ? DBNull.Value : companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdatePendingAsync(NpgsqlConnection connection, Guid id, PendingWhatsappAnalysisPayload payload, CancellationToken cancellationToken)
    {
        const string sql = """
            update ai_insights
            set message = @message,
                updated_at = now()
            where id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("message", JsonSerializer.Serialize(payload, SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateStatusAsync(NpgsqlConnection connection, Guid id, string status, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            update ai_insights
            set status = @status,
                message = case when @error is null then message else message || @error end,
                updated_at = now()
            where id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", string.IsNullOrWhiteSpace(error) ? DBNull.Value : $"\nerror: {error}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ReadConversationSummaryAsync(NpgsqlConnection connection, string conversationId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(conversationId, out var id))
        {
            return null;
        }

        const string sql = "select summary from whatsapp_conversations where id = @id";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<WhatsappAgentSettings> ReadWhatsappAgentSettingsAsync(NpgsqlConnection connection, Guid opportunityId, int fallbackDebounceMinutes, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                coalesce(settings.debounce_minutes, @fallbackDebounceMinutes) as debounce_minutes,
                settings.context_instructions
            from opportunities opportunity
            left join lateral (
                select debounce_minutes, context_instructions, company_id, updated_at, is_active
                from ai_agent_settings settings
                where settings.agent_key = 'whatsapp-conversation-analysis'
                  and (settings.company_id = opportunity.company_id or settings.company_id is null)
                order by case when settings.company_id = opportunity.company_id then 0 else 1 end, settings.updated_at desc
                limit 1
            ) settings on true
            where opportunity.id = @opportunityId
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("fallbackDebounceMinutes", fallbackDebounceMinutes);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new WhatsappAgentSettings(fallbackDebounceMinutes, null);
        }

        var debounceMinutes = reader.GetInt32(reader.GetOrdinal("debounce_minutes"));
        return new WhatsappAgentSettings(
            Math.Clamp(debounceMinutes, 1, 1440),
            ReadNullableString(reader, "context_instructions"));
    }

    private static async Task<bool> IsAgentActiveAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select exists (
                select 1
                from opportunities opportunity
                inner join lateral (
                    select is_active
                    from ai_agent_settings settings
                    where settings.agent_key = 'whatsapp-conversation-analysis'
                      and (settings.company_id = opportunity.company_id or settings.company_id is null)
                    order by case when settings.company_id = opportunity.company_id then 0 else 1 end, settings.updated_at desc
                    limit 1
                ) settings on true
                where opportunity.id = @opportunityId
                  and settings.is_active = true
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<DateTime?> ReadLastProcessedMessageAtAsync(NpgsqlConnection connection, Guid opportunityId, string conversationId, CancellationToken cancellationToken)
    {
        const string sql = """
            select message
            from ai_insights
            where kind = @kind
              and status in ('processed', 'applied')
              and opportunity_id = @opportunityId
              and message like @conversationToken
            order by updated_at desc
            limit 20
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("kind", Kind);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("conversationToken", $"%{conversationId}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var processedAt = TryReadLatestMessageAt(reader.GetString(reader.GetOrdinal("message")), conversationId);
            if (processedAt is not null)
            {
                return processedAt.Value.ToUniversalTime();
            }
        }

        return null;
    }

    private static DateTime? TryReadLatestMessageAt(string json, string conversationId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (HasConversationId(root, conversationId) && TryReadDateTime(root, "latestMessageAt", out var latestMessageAt))
            {
                return latestMessageAt;
            }

            if (root.TryGetProperty("data", out var data) &&
                HasConversationId(data, conversationId) &&
                TryReadDateTime(data, "latestMessageAt", out latestMessageAt))
            {
                return latestMessageAt;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool HasConversationId(JsonElement element, string conversationId) =>
        element.TryGetProperty("conversationId", out var value) &&
        string.Equals(value.GetString(), conversationId, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadDateTime(JsonElement element, string propertyName, out DateTime value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.TryGetDateTime(out value);
    }

    private static async Task<string> ReadTranscriptAsync(NpgsqlConnection connection, string conversationId, DateTime? after, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(conversationId, out var id))
        {
            return string.Empty;
        }

        const string sql = """
            select message.direction,
                   coalesce(message.sender_name, message.sender, message.direction) as sender_name,
                   message.message_type,
                   coalesce(nullif(message.text, ''), transcription.transcript) as text,
                   message.message_at
            from whatsapp_messages message
            left join lateral (
                select audio.transcript
                from whatsapp_message_audio_transcriptions audio
                where audio.whatsapp_message_id = message.id
                  and audio.status = 'ready'
                  and nullif(audio.transcript, '') is not null
                order by audio.updated_at desc
                limit 1
            ) transcription on true
            where message.conversation_id = @conversationId
              and message.status <> 'deleted'
              and (@after::timestamptz is null or message.message_at > @after::timestamptz)
            order by message.message_at desc
            limit 30
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", id);
        command.Parameters.Add("after", NpgsqlDbType.TimestampTz).Value = after is null ? DBNull.Value : after.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var direction = reader.GetString(reader.GetOrdinal("direction"));
            var senderName = reader.GetString(reader.GetOrdinal("sender_name"));
            var messageType = reader.GetString(reader.GetOrdinal("message_type"));
            var text = ReadNullableString(reader, "text") ?? $"[{messageType}]";
            var messageAt = reader.GetDateTime(reader.GetOrdinal("message_at")).ToUniversalTime();
            rows.Add($"{messageAt:yyyy-MM-dd HH:mm} {direction} {senderName}: {text}");
        }

        rows.Reverse();
        return string.Join("\n", rows);
    }

    private static async Task<Guid?> ReadOpportunityCompanyIdAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = "select company_id from opportunities where id = @opportunityId";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid companyId ? companyId : null;
    }

    private static PendingWhatsappAnalysis ReadPending(NpgsqlDataReader reader)
    {
        var payload = JsonSerializer.Deserialize<PendingWhatsappAnalysisPayload>(
            reader.GetString(reader.GetOrdinal("message")),
            SerializerOptions) ?? throw new InvalidOperationException("Invalid pending WhatsApp analysis payload.");

        return new PendingWhatsappAnalysis(
            reader.GetGuid(reader.GetOrdinal("id")),
            reader.GetGuid(reader.GetOrdinal("opportunity_id")),
            payload);
    }

    private static string? GetString(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string? FirstConfiguredValue(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record PendingWhatsappAnalysis(Guid Id, Guid OpportunityId, PendingWhatsappAnalysisPayload Payload);

    private sealed record WhatsappAgentSettings(int DebounceMinutes, string? Context);

    private sealed record PendingWhatsappAnalysisPayload(
        string ConversationId,
        string? ContactId,
        string FirstEventId,
        string LatestEventId,
        int MessageCount,
        DateTime FirstMessageAt,
        DateTime LatestMessageAt,
        string? LatestText);
}
