using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresWhatsappConversationActionStore(NpgsqlDataSource dataSource) : IWhatsappConversationActionStore
{
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

        if (result.ShouldCreateActivity && !string.IsNullOrWhiteSpace(result.ActivityTitle))
        {
            await InsertActivityAsync(connection, opportunityId, accountId, contactId, userId, companyId, result, cancellationToken);
            await InsertHistoryAsync(connection, opportunityId, userId, companyId, "Atividade criada pela IA a partir do WhatsApp", cancellationToken);
        }

        await InsertInsightAsync(connection, opportunityId, companyId, context, result, cancellationToken);
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

    private static async Task InsertActivityAsync(
        NpgsqlConnection connection,
        Guid opportunityId,
        Guid? accountId,
        Guid? contactId,
        Guid? userId,
        Guid? companyId,
        WhatsappConversationAnalysisResult result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into activities
                (id, opportunity_id, account_id, contact_id, owner_user_id, title, activity_type, channel, status, date_at, notes, company_id, created_at, updated_at)
            values
                (@id, @opportunityId, @accountId, @contactId, @userId, @title, 'follow-up', 'whatsapp', 'pending', @dateAt, @notes, @companyId, now(), now())
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("accountId", accountId is null ? DBNull.Value : accountId.Value);
        command.Parameters.AddWithValue("contactId", contactId is null ? DBNull.Value : contactId.Value);
        command.Parameters.AddWithValue("userId", userId is null ? DBNull.Value : userId.Value);
        command.Parameters.AddWithValue("title", result.ActivityTitle ?? "Fazer follow-up pelo WhatsApp");
        command.Parameters.AddWithValue("dateAt", result.ActivityDueAt ?? DateTime.UtcNow.AddDays(1));
        command.Parameters.AddWithValue("notes", string.IsNullOrWhiteSpace(result.ActivityNotes) ? DBNull.Value : result.ActivityNotes);
        command.Parameters.AddWithValue("companyId", companyId is null ? DBNull.Value : companyId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
}
