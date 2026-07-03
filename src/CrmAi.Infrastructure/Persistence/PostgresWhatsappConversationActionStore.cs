using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresWhatsappConversationActionStore(NpgsqlDataSource dataSource) : IWhatsappConversationActionStore
{
    private const string SkoposActivityType = "Agente Skopos";
    private const string WhatsappChannel = "whatsapp";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ApplyAsync(OpportunityAnalysisContext context, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Opportunity.Id, out var opportunityId))
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (await WasProcessedAsync(connection, context.TriggerEvent.EventId, cancellationToken))
        {
            return;
        }

        var userId = ResolveUserId(context);
        var runId = GetGuid(context.TriggerEvent, "runId");
        var contactId = GetGuid(context.TriggerEvent, "contactId");
        var conversationId = GetGuid(context.TriggerEvent, "conversationId");
        var accountId = Guid.TryParse(context.Opportunity.AccountId, out var parsedAccountId) ? parsedAccountId : (Guid?)null;
        var companyId = await ReadOpportunityCompanyIdAsync(connection, opportunityId, cancellationToken);

        if (conversationId is not null && !string.IsNullOrWhiteSpace(result.ConversationSummary))
        {
            await UpdateConversationSummaryAsync(connection, conversationId.Value, result.ConversationSummary, cancellationToken);
        }

        if (result.ShouldCreateNote && !string.IsNullOrWhiteSpace(result.NoteText))
        {
            await InsertNoteAsync(connection, opportunityId, accountId, contactId, userId, companyId, result.NoteText, cancellationToken);
            await InsertHistoryAsync(connection, opportunityId, userId, companyId, "Nota criada pela IA a partir do WhatsApp", cancellationToken);
        }

        var activityWasCreated = await UpsertDailySkoposActivityAsync(
            connection,
            opportunityId,
            accountId,
            contactId,
            userId,
            companyId,
            conversationId,
            context,
            result,
            cancellationToken);
        await InsertHistoryAsync(
            connection,
            opportunityId,
            userId,
            companyId,
            activityWasCreated
                ? "Atividade Agente Skopos criada a partir do WhatsApp"
                : "Atividade Agente Skopos atualizada a partir do WhatsApp",
            cancellationToken);

        await InsertInsightAsync(connection, opportunityId, companyId, context, result, cancellationToken);
        await CompleteQueuedRunAsync(connection, runId, conversationId, result.ConversationSummary, cancellationToken);
    }

    private static async Task<bool> WasProcessedAsync(NpgsqlConnection connection, string eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            select exists (
                select 1
                from ai_insights
                where kind = 'whatsapp-conversation-analysis'
                  and message like @eventToken
            )
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("eventToken", $"%{eventId}%");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<Guid?> ReadOpportunityCompanyIdAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = "select company_id from opportunities where id = @opportunityId";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid companyId ? companyId : null;
    }

    private static async Task InsertNoteAsync(
        NpgsqlConnection connection,
        Guid opportunityId,
        Guid? accountId,
        Guid? contactId,
        Guid? userId,
        Guid? companyId,
        string text,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into notes (id, opportunity_id, account_id, contact_id, author_user_id, text, company_id, created_at, updated_at)
            values (@id, @opportunityId, @accountId, @contactId, @userId, @text, @companyId, now(), now())
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("accountId", accountId is null ? DBNull.Value : accountId.Value);
        command.Parameters.AddWithValue("contactId", contactId is null ? DBNull.Value : contactId.Value);
        command.Parameters.AddWithValue("userId", userId is null ? DBNull.Value : userId.Value);
        command.Parameters.AddWithValue("text", text);
        command.Parameters.AddWithValue("companyId", companyId is null ? DBNull.Value : companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateConversationSummaryAsync(NpgsqlConnection connection, Guid conversationId, string summary, CancellationToken cancellationToken)
    {
        const string sql = """
            update whatsapp_conversations
            set summary = @summary,
                updated_at = now()
            where id = @conversationId
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        command.Parameters.AddWithValue("summary", summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> UpsertDailySkoposActivityAsync(
        NpgsqlConnection connection,
        Guid opportunityId,
        Guid? accountId,
        Guid? contactId,
        Guid? userId,
        Guid? companyId,
        Guid? conversationId,
        OpportunityAnalysisContext context,
        WhatsappConversationAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var activityDate = ResolveActivityDate(context);
        var marker = conversationId is null ? $"[whatsapp_event_id:{context.TriggerEvent.EventId}]" : $"[whatsapp_conversation_id:{conversationId}]";
        var notesBlock = BuildActivityNotesBlock(context, result, marker);
        var completedNotes = Truncate(result.ConversationSummary, 2000);

        var existingActivityId = await FindDailySkoposActivityAsync(
            connection,
            opportunityId,
            contactId,
            conversationId,
            activityDate.Date,
            marker,
            cancellationToken);

        if (existingActivityId is not null)
        {
            const string updateSql = """
                update activities
                set notes = concat_ws(E'\n\n', nullif(notes, ''), @notesBlock),
                    completion_notes = @completedNotes,
                    status = 'done',
                    updated_at = now()
                where id = @activityId;

                update opportunities
                set last_activity_at = greatest(coalesce(last_activity_at, @activityDate), @activityDate),
                    updated_at = now()
                where id = @opportunityId;
                """;

            await using var updateCommand = new NpgsqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("activityId", existingActivityId.Value);
            updateCommand.Parameters.AddWithValue("opportunityId", opportunityId);
            updateCommand.Parameters.AddWithValue("activityDate", activityDate);
            updateCommand.Parameters.AddWithValue("notesBlock", notesBlock);
            updateCommand.Parameters.AddWithValue("completedNotes", completedNotes);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            return false;
        }

        const string insertSql = """
            insert into activities
                (id, opportunity_id, account_id, contact_id, owner_user_id, title, activity_type, channel, status, date_at, notes, completion_notes, company_id, created_at, updated_at)
            values
                (@id, @opportunityId, @accountId, @contactId, @userId, @title, @activityType, @channel, 'done', @dateAt, @notes, @completedNotes, @companyId, now(), now());

            update opportunities
            set last_activity_at = greatest(coalesce(last_activity_at, @dateAt), @dateAt),
                updated_at = now()
            where id = @opportunityId;
            """;

        await using var command = new NpgsqlCommand(insertSql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("accountId", accountId is null ? DBNull.Value : accountId.Value);
        command.Parameters.AddWithValue("contactId", contactId is null ? DBNull.Value : contactId.Value);
        command.Parameters.AddWithValue("userId", userId is null ? DBNull.Value : userId.Value);
        command.Parameters.AddWithValue("title", "Conversa WhatsApp analisada pelo Agente Skopos");
        command.Parameters.AddWithValue("activityType", SkoposActivityType);
        command.Parameters.AddWithValue("channel", WhatsappChannel);
        command.Parameters.AddWithValue("dateAt", activityDate);
        command.Parameters.AddWithValue("notes", notesBlock);
        command.Parameters.AddWithValue("completedNotes", completedNotes);
        command.Parameters.AddWithValue("companyId", companyId is null ? DBNull.Value : companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async Task<Guid?> FindDailySkoposActivityAsync(
        NpgsqlConnection connection,
        Guid opportunityId,
        Guid? contactId,
        Guid? conversationId,
        DateTime activityDate,
        string marker,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from activities
            where opportunity_id = @opportunityId
              and activity_type = @activityType
              and channel = @channel
              and date_at::date = @activityDate
              and (@contactId is null or contact_id = @contactId)
              and (
                  notes like @markerPattern
                  or (@conversationId is null and notes like @fallbackPattern)
              )
            order by updated_at desc
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.Add("contactId", NpgsqlDbType.Uuid).Value = contactId is null ? DBNull.Value : contactId.Value;
        command.Parameters.Add("conversationId", NpgsqlDbType.Uuid).Value = conversationId is null ? DBNull.Value : conversationId.Value;
        command.Parameters.Add("activityDate", NpgsqlDbType.Date).Value = DateOnly.FromDateTime(activityDate);
        command.Parameters.AddWithValue("activityType", SkoposActivityType);
        command.Parameters.AddWithValue("channel", WhatsappChannel);
        command.Parameters.AddWithValue("markerPattern", $"%{marker}%");
        command.Parameters.AddWithValue("fallbackPattern", "%[whatsapp_event_id:%");

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static DateTime ResolveActivityDate(OpportunityAnalysisContext context)
    {
        var latestMessageAt = GetDateTime(context.TriggerEvent, "latestMessageAt");
        return (latestMessageAt ?? context.TriggerEvent.OccurredAt).ToUniversalTime();
    }

    private static string BuildActivityNotesBlock(
        OpportunityAnalysisContext context,
        WhatsappConversationAnalysisResult result,
        string marker)
    {
        var analyzedAt = DateTime.UtcNow;
        var transcript = GetString(context.TriggerEvent, "text");
        var lines = new List<string>
        {
            marker,
            $"Atualizacao em {analyzedAt:yyyy-MM-dd HH:mm} UTC",
            "",
            "Resumo:",
            result.ConversationSummary
        };

        if (result.ShouldCreateActivity && !string.IsNullOrWhiteSpace(result.ActivityTitle))
        {
            lines.Add("");
            lines.Add("Acao sugerida:");
            lines.Add(result.ActivityTitle);
            if (!string.IsNullOrWhiteSpace(result.ActivityNotes))
            {
                lines.Add(result.ActivityNotes);
            }
        }

        if (result.ShouldCreateNote && !string.IsNullOrWhiteSpace(result.NoteText))
        {
            lines.Add("");
            lines.Add("Nota sugerida:");
            lines.Add(result.NoteText);
        }

        if (result.Reasons.Count > 0)
        {
            lines.Add("");
            lines.Add("Motivos:");
            lines.AddRange(result.Reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Select(reason => "- " + reason.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            lines.Add("");
            lines.Add("Trecho analisado:");
            lines.Add(transcript.Trim());
        }

        return string.Join("\n", lines).Trim();
    }

    private static async Task InsertHistoryAsync(NpgsqlConnection connection, Guid opportunityId, Guid? userId, Guid? companyId, string message, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into opportunity_history (id, opportunity_id, user_id, event, company_id, created_at)
            values (@id, @opportunityId, @userId, @message, @companyId, now())
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("userId", userId is null ? DBNull.Value : userId.Value);
        command.Parameters.AddWithValue("message", message);
        command.Parameters.AddWithValue("companyId", companyId is null ? DBNull.Value : companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInsightAsync(
        NpgsqlConnection connection,
        Guid opportunityId,
        Guid? companyId,
        OpportunityAnalysisContext context,
        WhatsappConversationAnalysisResult result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ai_insights (id, opportunity_id, title, message, kind, confidence, status, company_id, created_at, updated_at)
            values (@id, @opportunityId, @title, @message, 'whatsapp-conversation-analysis', @confidence, 'applied', @companyId, now(), now())
            """;

        var payload = new
        {
            eventId = context.TriggerEvent.EventId,
            context.TriggerEvent.Type,
            context.TriggerEvent.Data,
            result.ShouldCreateNote,
            result.ShouldCreateActivity,
            result.ConversationSummary,
            result.Reasons
        };

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("title", "Analise de conversa WhatsApp");
        command.Parameters.AddWithValue("message", JsonSerializer.Serialize(payload, SerializerOptions));
        command.Parameters.AddWithValue("confidence", Math.Clamp(result.ConfidenceScore, 0, 100) / 100m);
        command.Parameters.AddWithValue("companyId", companyId is null ? DBNull.Value : companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteQueuedRunAsync(
        NpgsqlConnection connection,
        Guid? runId,
        Guid? conversationId,
        string summary,
        CancellationToken cancellationToken)
    {
        if (runId is null)
        {
            return;
        }

        const string sql = """
            update whatsapp_conversation_analysis_runs
            set status = 'completed',
                summary = @summary,
                error = null,
                updated_at = now()
            where id = @runId;

            update whatsapp_conversations
            set summary = @summary,
                last_analysis_status = 'completed',
                last_analysis_at = now(),
                updated_at = now()
            where @conversationId is not null
              and id = @conversationId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("runId", runId.Value);
        command.Parameters.Add("conversationId", NpgsqlDbType.Uuid).Value = conversationId is null ? DBNull.Value : conversationId.Value;
        command.Parameters.AddWithValue("summary", summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Guid? ResolveUserId(OpportunityAnalysisContext context)
    {
        var fromEvent = GetGuid(context.TriggerEvent, "ownerUserId");
        if (fromEvent is not null)
        {
            return fromEvent;
        }

        return Guid.TryParse(context.TriggerEvent.UserId ?? context.Opportunity.OwnerUserId, out var userId)
            ? userId
            : null;
    }

    private static Guid? GetGuid(OpportunityEvent opportunityEvent, string key)
    {
        if (!opportunityEvent.Data.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return Guid.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static string? GetString(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static DateTime? GetDateTime(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) && DateTime.TryParse(value?.ToString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
