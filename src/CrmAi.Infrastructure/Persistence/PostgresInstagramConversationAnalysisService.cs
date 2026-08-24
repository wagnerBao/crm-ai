using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresInstagramConversationAnalysisService(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    IOpportunityContextRepository contextRepository,
    IOpenAiWhatsappConversationAnalysisClient openAiClient) : IInstagramConversationAnalysisService
{
    private const string AgentKey = "instagram-conversation-analysis";

    public async Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        if (!TryGetGuid(opportunityEvent, "conversationId", out var conversationId))
        {
            return;
        }
        var opportunityId = Guid.TryParse(opportunityEvent.OpportunityId, out var parsedOpportunityId)
            && parsedOpportunityId != Guid.Empty
                ? parsedOpportunityId
                : (Guid?)null;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var conversation = await ReadConversationAsync(connection, conversationId, cancellationToken);
        if (conversation is null || conversation.CompanyId is null)
        {
            return;
        }

        if (await WasProcessedAsync(connection, opportunityId, opportunityEvent.EventId, cancellationToken))
        {
            return;
        }

        var settings = await settingsRepository.GetAsync(AgentKey, conversation.CompanyId.ToString(), cancellationToken);
        if (!settings.IsActive)
        {
            return;
        }

        var data = new Dictionary<string, object?>(opportunityEvent.Data, StringComparer.OrdinalIgnoreCase)
        {
            ["previousSummary"] = conversation.Summary,
            ["contactId"] = conversation.ContactId?.ToString(),
            ["contactName"] = conversation.ContactName,
            ["ownerUserId"] = conversation.OwnerUserId?.ToString(),
            ["latestMessageAt"] = opportunityEvent.OccurredAt,
            ["additionalContext"] = settings.ContextInstructions
        };
        var enrichedEvent = opportunityEvent with { Data = data };
        var context = opportunityId is null
            ? null
            : await contextRepository.GetForAnalysisAsync(enrichedEvent, cancellationToken);
        var input = context is null
            ? WhatsappConversationAnalysisInput.FromContactEvent(enrichedEvent)
            : WhatsappConversationAnalysisInput.FromContext(context, settings.ContextEntityKeys);
        if (string.IsNullOrWhiteSpace(input.NewTranscript))
        {
            return;
        }

        var invocationContext = new AiAgentInvocationContext(
            PlatformArea: "instagram",
            CompanyId: conversation.CompanyId.ToString(),
            OpportunityId: opportunityId?.ToString(),
            AccountId: conversation.AccountId?.ToString(),
            ContactId: conversation.ContactId?.ToString(),
            UserId: opportunityEvent.UserId ?? conversation.OwnerUserId?.ToString(),
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["triggerEventId"] = opportunityEvent.EventId,
                ["triggerEventType"] = opportunityEvent.Type,
                ["channel"] = "instagram",
                ["conversationId"] = conversationId
            });

        var response = await openAiClient.AnalyzeAsync(settings, input, invocationContext, cancellationToken);
        var summary = FirstNotEmpty(response.ConversationSummary, conversation.Summary, input.NewTranscript);
        await PersistAsync(connection, conversation, opportunityId, opportunityEvent, response, summary, cancellationToken);
    }

    private static async Task<InstagramConversationRow?> ReadConversationAsync(
        NpgsqlConnection connection,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select c.id, c.company_id, c.contact_id, c.owner_user_id, contact.account_id,
                   c.contact_name, c.summary
            from instagram_conversations c
            left join contacts contact on contact.id = c.contact_id
            where c.id = @conversationId and c.channel = 'instagram'
            limit 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InstagramConversationRow(
            reader.GetGuid(reader.GetOrdinal("id")),
            ReadNullableGuid(reader, "company_id"),
            ReadNullableGuid(reader, "contact_id"),
            ReadNullableGuid(reader, "owner_user_id"),
            ReadNullableGuid(reader, "account_id"),
            reader.GetString(reader.GetOrdinal("contact_name")),
            ReadNullableString(reader, "summary"));
    }

    private static async Task<bool> WasProcessedAsync(
        NpgsqlConnection connection,
        Guid? opportunityId,
        string eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select exists (
                select 1 from ai_insights
                where opportunity_id is not distinct from @opportunityId
                  and kind = @kind
                  and message like @eventMarker
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("opportunityId", NpgsqlDbType.Uuid).Value = opportunityId is null ? DBNull.Value : opportunityId.Value;
        command.Parameters.AddWithValue("kind", AgentKey);
        command.Parameters.AddWithValue("eventMarker", $"%{eventId}%");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task PersistAsync(
        NpgsqlConnection connection,
        InstagramConversationRow conversation,
        Guid? opportunityId,
        OpportunityEvent opportunityEvent,
        OpenAiWhatsappConversationAnalysisResponse response,
        string summary,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", $"instagram-analysis:{conversation.Id}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var summaryCommand = new NpgsqlCommand(
            "update instagram_conversations set summary = @summary, updated_at = now() where id = @conversationId and company_id = @companyId;",
            connection,
            transaction))
        {
            summaryCommand.Parameters.AddWithValue("summary", summary);
            summaryCommand.Parameters.AddWithValue("conversationId", conversation.Id);
            summaryCommand.Parameters.AddWithValue("companyId", conversation.CompanyId!.Value);
            await summaryCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (response.ShouldCreateNote
            && !string.IsNullOrWhiteSpace(response.NoteText)
            && (opportunityId is not null || conversation.ContactId is not null))
        {
            const string noteSql = """
                insert into notes
                    (id, account_id, contact_id, opportunity_id, author_user_id, text, company_id, created_at, updated_at)
                values
                    (@id, @accountId, @contactId, @opportunityId, @ownerUserId, @text, @companyId, now(), now());
                """;
            await using var noteCommand = new NpgsqlCommand(noteSql, connection, transaction);
            noteCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            AddNullableGuid(noteCommand, "accountId", conversation.AccountId);
            AddNullableGuid(noteCommand, "contactId", conversation.ContactId);
            AddNullableGuid(noteCommand, "opportunityId", opportunityId);
            AddNullableGuid(noteCommand, "ownerUserId", conversation.OwnerUserId);
            noteCommand.Parameters.AddWithValue("text", response.NoteText.Trim());
            noteCommand.Parameters.AddWithValue("companyId", conversation.CompanyId.Value);
            await noteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (response.ShouldCreateActivity && !string.IsNullOrWhiteSpace(response.ActivityTitle))
        {
            const string activitySql = """
                insert into activities
                    (id, account_id, opportunity_id, contact_id, owner_user_id, title, activity_type, channel,
                     status, date_at, notes, company_id, created_at, updated_at)
                values
                    (@id, @accountId, @opportunityId, @contactId, @ownerUserId, @title, 'follow-up', 'instagram',
                     'pending', @dateAt, @notes, @companyId, now(), now());
                """;
            await using var activityCommand = new NpgsqlCommand(activitySql, connection, transaction);
            activityCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            AddNullableGuid(activityCommand, "accountId", conversation.AccountId);
            AddNullableGuid(activityCommand, "opportunityId", opportunityId);
            AddNullableGuid(activityCommand, "contactId", conversation.ContactId);
            AddNullableGuid(activityCommand, "ownerUserId", conversation.OwnerUserId);
            activityCommand.Parameters.AddWithValue("title", response.ActivityTitle.Trim());
            activityCommand.Parameters.AddWithValue("dateAt", ParseDateTime(response.ActivityDueAt) ?? DateTime.UtcNow.AddDays(1));
            activityCommand.Parameters.Add("notes", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(response.ActivityNotes) ? DBNull.Value : response.ActivityNotes.Trim();
            activityCommand.Parameters.AddWithValue("companyId", conversation.CompanyId.Value);
            await activityCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insightSql = """
            insert into ai_insights
                (id, opportunity_id, title, message, kind, confidence, status, company_id, created_at, updated_at)
            values
                (@id, @opportunityId, 'Analise de conversa Instagram', @message, @kind, @confidence, 'applied', @companyId, now(), now());
            """;
        await using (var insightCommand = new NpgsqlCommand(insightSql, connection, transaction))
        {
            insightCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            AddNullableGuid(insightCommand, "opportunityId", opportunityId);
            insightCommand.Parameters.AddWithValue("message", JsonSerializer.Serialize(new
            {
                eventId = opportunityEvent.EventId,
                conversationId = conversation.Id,
                conversationSummary = summary,
                response.CommercialObservations,
                response.NextSteps,
                response.Insights,
                response.Reasons
            }));
            insightCommand.Parameters.AddWithValue("kind", AgentKey);
            insightCommand.Parameters.AddWithValue("confidence", Math.Clamp(response.ConfidenceScore, 0, 100) / 100m);
            insightCommand.Parameters.AddWithValue("companyId", conversation.CompanyId.Value);
            await insightCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool TryGetGuid(OpportunityEvent opportunityEvent, string key, out Guid value)
    {
        value = Guid.Empty;
        return opportunityEvent.Data.TryGetValue(key, out var raw) && Guid.TryParse(raw?.ToString(), out value);
    }

    private static Guid? ReadNullableGuid(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static void AddNullableGuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value is null ? DBNull.Value : value.Value;

    private static DateTime? ParseDateTime(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "Conversa do Instagram analisada.";

    private sealed record InstagramConversationRow(
        Guid Id,
        Guid? CompanyId,
        Guid? ContactId,
        Guid? OwnerUserId,
        Guid? AccountId,
        string ContactName,
        string? Summary);
}
