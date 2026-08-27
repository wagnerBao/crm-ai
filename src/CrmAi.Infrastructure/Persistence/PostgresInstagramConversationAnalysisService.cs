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
    IWhatsappSuggestionContextRepository suggestionContextRepository,
    IOpenAiWhatsappConversationAnalysisClient openAiClient) : IInstagramConversationAnalysisService
{
    private const string AgentKey = "instagram-conversation-analysis";
    private const string SkoposActivityType = "agent-skopos";
    private const string InstagramChannel = "instagram";

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

        var semanticContext = await suggestionContextRepository.GetAsync(
            conversation.CompanyId.ToString(),
            conversation.ContactId?.ToString(),
            AgentKey,
            cancellationToken);
        input = input with
        {
            ExistingSuggestions = semanticContext.ExistingSuggestions,
            ExistingOpenOpportunities = semanticContext.ExistingOpenOpportunities
        };

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
        await PersistAsync(connection, conversation, opportunityId, opportunityEvent, response, summary, settings.Model, cancellationToken);
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
        string? generationModel,
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
            """
            update instagram_conversations
            set summary = @summary,
                last_analyzed_message_at = greatest(coalesce(last_analyzed_message_at, '-infinity'::timestamptz), @processedUntil),
                last_analysis_status = 'completed',
                last_analysis_at = now(),
                updated_at = now()
            where id = @conversationId and company_id = @companyId;
            """,
            connection,
            transaction))
        {
            summaryCommand.Parameters.AddWithValue("summary", summary);
            summaryCommand.Parameters.AddWithValue("processedUntil", ReadEventDate(opportunityEvent, "processedUntil") ?? opportunityEvent.OccurredAt);
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

        if (opportunityId is not null || conversation.ContactId is not null)
        {
            await UpsertConversationActivityAsync(
                connection,
                transaction,
                conversation,
                opportunityId,
                opportunityEvent,
                summary,
                cancellationToken);
        }

        await UpsertAgentSuggestionsAsync(
            connection,
            transaction,
            conversation,
            opportunityId,
            opportunityEvent,
            response,
            generationModel,
            cancellationToken);

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

    private static async Task UpsertConversationActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InstagramConversationRow conversation,
        Guid? opportunityId,
        OpportunityEvent opportunityEvent,
        string summary,
        CancellationToken cancellationToken)
    {
        var activityId = Guid.NewGuid();
        var activityDate = ReadEventDate(opportunityEvent, "latestMessageAt") ?? opportunityEvent.OccurredAt;
        var eventMarker = $"[agent_skopos_event:{opportunityEvent.EventId}]";
        var notes = string.Join("\n", new[]
        {
            "Atividade criada automaticamente pelo Agent Skopos após análise da conversa do Instagram.",
            $"[instagram_conversation_id:{conversation.Id}]",
            eventMarker,
            "",
            "Resumo:",
            summary
        });

        const string sql = """
            with settings as (
                select coalesce(
                    (select time_zone_id from daily_checkout_settings where company_id = @companyId limit 1),
                    'America/Sao_Paulo') as time_zone_id
            ), existing as (
                select activity.id
                from activities activity
                cross join settings
                where activity.company_id = @companyId
                  and activity.activity_type = @activityType
                  and activity.channel = @channel
                  and (
                    (@contactId is not null and activity.contact_id = @contactId)
                    or (@contactId is null and @opportunityId is not null and activity.opportunity_id = @opportunityId)
                  )
                  and (activity.date_at at time zone settings.time_zone_id)::date =
                      (@dateAt at time zone settings.time_zone_id)::date
                order by activity.created_at
                limit 1
            ), changed as (
                update activities activity
                set status = 'done',
                    date_at = greatest(activity.date_at, @dateAt),
                    updated_at = now()
                where activity.id = (select id from existing)
                returning activity.id
            )
            insert into activities
                (id, account_id, opportunity_id, contact_id, owner_user_id, title, activity_type, channel,
                 status, date_at, notes, completion_notes, completed_notes, company_id, created_at, updated_at)
            select
                @id, @accountId, @opportunityId, @contactId, @ownerUserId,
                'Conversa Instagram analisada pelo Agent Skopos', @activityType, @channel,
                'done', @dateAt, @notes, @completionNotes, @completionNotes, @companyId, now(), now()
            where not exists (select 1 from changed);
            """;

        await using (var activityCommand = new NpgsqlCommand(sql, connection, transaction))
        {
            activityCommand.Parameters.AddWithValue("id", activityId);
            AddNullableGuid(activityCommand, "accountId", conversation.AccountId);
            AddNullableGuid(activityCommand, "opportunityId", opportunityId);
            AddNullableGuid(activityCommand, "contactId", conversation.ContactId);
            AddNullableGuid(activityCommand, "ownerUserId", conversation.OwnerUserId);
            activityCommand.Parameters.AddWithValue("companyId", conversation.CompanyId!.Value);
            activityCommand.Parameters.AddWithValue("activityType", SkoposActivityType);
            activityCommand.Parameters.AddWithValue("channel", InstagramChannel);
            activityCommand.Parameters.AddWithValue("dateAt", activityDate.ToUniversalTime());
            activityCommand.Parameters.AddWithValue("notes", notes);
            activityCommand.Parameters.AddWithValue("completionNotes", summary.Length <= 2000 ? summary : summary[..2000]);
            await activityCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (opportunityId is not null)
        {
            await using var opportunityCommand = new NpgsqlCommand(
                "update opportunities set last_activity_at = greatest(coalesce(last_activity_at, @dateAt), @dateAt), updated_at = now() where id = @opportunityId;",
                connection,
                transaction);
            opportunityCommand.Parameters.AddWithValue("opportunityId", opportunityId.Value);
            opportunityCommand.Parameters.AddWithValue("dateAt", activityDate.ToUniversalTime());
            await opportunityCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertAgentSuggestionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InstagramConversationRow conversation,
        Guid? opportunityId,
        OpportunityEvent opportunityEvent,
        OpenAiWhatsappConversationAnalysisResponse response,
        string? generationModel,
        CancellationToken cancellationToken)
    {
        if (conversation.ContactId is null || conversation.CompanyId is null)
        {
            return;
        }

        var runId = Guid.TryParse(opportunityEvent.EventId, out var parsedRunId)
            ? parsedRunId
            : (Guid?)null;

        if (response.ShouldCreateActivity && !string.IsNullOrWhiteSpace(response.ActivityTitle))
        {
            var description = string.IsNullOrWhiteSpace(response.ActivityNotes)
                ? response.ActivityTitle
                : response.ActivityNotes;
            var payload = JsonSerializer.Serialize(new
            {
                activityType = "follow-up",
                channel = InstagramChannel,
                dueAt = response.ActivityDueAt,
                notes = response.ActivityNotes,
                semanticIntentKey = response.ActivityIntentKey
            });
            await UpsertAgentSuggestionAsync(
                connection,
                transaction,
                conversation.CompanyId.Value,
                conversation.ContactId.Value,
                conversation.Id,
                runId,
                "activity",
                response.ActivityTitle,
                description!,
                ParseDateTime(response.ActivityDueAt),
                payload,
                response.ActivityMatchingSuggestionId,
                response.ActivityIntentKey,
                generationModel,
                response,
                cancellationToken);
        }

        if (response.ShouldCreateOpportunity
            && !string.IsNullOrWhiteSpace(response.OpportunityTitle)
            && !await HasMatchedOpenOpportunityAsync(
                connection,
                transaction,
                conversation.ContactId.Value,
                opportunityId,
                response.MatchingOpenOpportunityId,
                cancellationToken))
        {
            var description = string.IsNullOrWhiteSpace(response.OpportunityDescription)
                ? response.OpportunityTitle
                : response.OpportunityDescription;
            var payload = JsonSerializer.Serialize(new
            {
                origin = "Instagram",
                description = response.OpportunityDescription,
                semanticIntentKey = response.OpportunityIntentKey
            });
            await UpsertAgentSuggestionAsync(
                connection,
                transaction,
                conversation.CompanyId.Value,
                conversation.ContactId.Value,
                conversation.Id,
                runId,
                "opportunity",
                response.OpportunityTitle,
                description!,
                null,
                payload,
                response.OpportunityMatchingSuggestionId,
                response.OpportunityIntentKey,
                generationModel,
                response,
                cancellationToken);
        }
    }

    private static async Task UpsertAgentSuggestionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        Guid contactId,
        Guid conversationId,
        Guid? runId,
        string suggestionType,
        string title,
        string description,
        DateTime? dueAt,
        string payload,
        string? matchingSuggestionId,
        string? semanticIntentKey,
        string? generationModel,
        OpenAiWhatsappConversationAnalysisResponse response,
        CancellationToken cancellationToken)
    {
        const string sql = """
            with existing as (
                select id
                from ai_agent_suggestions
                where company_id = @companyId
                  and agent_key = @agentKey
                  and suggestion_type = @suggestionType
                  and contact_id = @contactId
                  and status in ('pending', 'rejected')
                  and (
                    id = @matchingSuggestionId
                    or (@semanticIntentKey is not null and payload ->> 'semanticIntentKey' = @semanticIntentKey)
                  )
                order by case when status = 'pending' then 0 else 1 end, updated_at desc
                limit 1
            ), changed as (
                update ai_agent_suggestions suggestion
                set conversation_id = @conversationId,
                    run_id = @runId,
                    title = @title,
                    description = @description,
                    suggested_due_at = @dueAt,
                    payload = @payload,
                    generation_model = @generationModel,
                    confidence_score = @confidenceScore,
                    generation_reasons = @generationReasons,
                    updated_at = now()
                where suggestion.id = (select id from existing)
                returning suggestion.id
            )
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, confidence_score,
                 generation_reasons, created_at, updated_at)
            select
                @id, @companyId, @agentKey, @suggestionType, 'pending', @contactId, @conversationId, @runId,
                @title, @description, @dueAt, @payload, @generationModel, @confidenceScore,
                @generationReasons, now(), now()
            where not exists (select 1 from changed)
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("agentKey", AgentKey);
        command.Parameters.AddWithValue("suggestionType", suggestionType);
        command.Parameters.AddWithValue("contactId", contactId);
        command.Parameters.AddWithValue("conversationId", conversationId);
        command.Parameters.Add("runId", NpgsqlDbType.Uuid).Value = runId is null ? DBNull.Value : runId.Value;
        command.Parameters.Add("matchingSuggestionId", NpgsqlDbType.Uuid).Value =
            Guid.TryParse(matchingSuggestionId, out var parsedSuggestionId) ? parsedSuggestionId : DBNull.Value;
        command.Parameters.Add("semanticIntentKey", NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(semanticIntentKey) ? DBNull.Value : semanticIntentKey.Trim();
        command.Parameters.AddWithValue("title", Truncate(title.Trim(), 300));
        command.Parameters.AddWithValue("description", Truncate(description.Trim(), 3000));
        command.Parameters.Add("dueAt", NpgsqlDbType.TimestampTz).Value = dueAt is null ? DBNull.Value : dueAt.Value;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.Add("generationModel", NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(generationModel) ? DBNull.Value : generationModel;
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(response.ConfidenceScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(response.Reasons);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasMatchedOpenOpportunityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid contactId,
        Guid? currentOpportunityId,
        string? matchingOpenOpportunityId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(matchingOpenOpportunityId, out var parsedOpportunityId))
        {
            return false;
        }

        const string sql = """
            select exists (
                select 1
                from opportunities opportunity
                where opportunity.status = 'active'
                  and opportunity.id = @matchingOpenOpportunityId
                  and (
                    opportunity.id = @currentOpportunityId
                    or exists (
                        select 1 from opportunity_contacts link
                        where link.opportunity_id = opportunity.id
                          and link.contact_id = @contactId
                    )
                    or opportunity.account_id = (select account_id from contacts where id = @contactId)
                  )
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("matchingOpenOpportunityId", parsedOpportunityId);
        AddNullableGuid(command, "currentOpportunityId", currentOpportunityId);
        command.Parameters.AddWithValue("contactId", contactId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static bool TryGetGuid(OpportunityEvent opportunityEvent, string key, out Guid value)
    {
        value = Guid.Empty;
        return opportunityEvent.Data.TryGetValue(key, out var raw) && Guid.TryParse(raw?.ToString(), out value);
    }

    private static DateTime? ReadEventDate(OpportunityEvent opportunityEvent, string key)
    {
        if (!opportunityEvent.Data.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return DateTime.TryParse(raw.ToString(), out var parsed) ? parsed.ToUniversalTime() : null;
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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record InstagramConversationRow(
        Guid Id,
        Guid? CompanyId,
        Guid? ContactId,
        Guid? OwnerUserId,
        Guid? AccountId,
        string ContactName,
        string? Summary);
}
