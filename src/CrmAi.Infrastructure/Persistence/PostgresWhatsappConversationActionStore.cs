using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresWhatsappConversationActionStore(NpgsqlDataSource dataSource) : IWhatsappConversationActionStore
{
    private const string SkoposActivityType = "agent-skopos";
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

        var activity = await UpsertSkoposAnalysisActivityAsync(
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
        if (activity.WasCreated)
        {
            await InsertHistoryAsync(
                connection,
                opportunityId,
                userId,
                companyId,
                "Atividade criada automaticamente pelo Agent Skopos a partir do WhatsApp",
                cancellationToken);
        }

        await PersistStructuredAnalysisAndScorecardAsync(
            connection, runId, activity.Id, companyId, userId, result, cancellationToken);
        await InsertAgentSuggestionsAsync(connection, companyId, contactId, conversationId, runId, opportunityId, result, cancellationToken);
        await InsertInsightAsync(connection, opportunityId, companyId, context, result, cancellationToken);
        await CompleteQueuedRunAsync(connection, runId, conversationId, result.ConversationSummary, cancellationToken);
    }

    public async Task ApplyContactAsync(OpportunityEvent opportunityEvent, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken)
    {
        var runId = GetGuid(opportunityEvent, "runId");
        var conversationId = GetGuid(opportunityEvent, "conversationId");
        var contactId = GetGuid(opportunityEvent, "contactId");
        var companyId = GetGuid(opportunityEvent, "companyId");
        var accountId = GetGuid(opportunityEvent, "accountId");
        var userId = GetGuid(opportunityEvent, "ownerUserId")
            ?? (Guid.TryParse(opportunityEvent.UserId, out var parsedUserId) ? parsedUserId : null);

        if (runId is null || conversationId is null || contactId is null || companyId is null)
        {
            throw new InvalidOperationException("Contact-only WhatsApp analysis requires runId, conversationId, contactId and companyId.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (await IsRunCompletedAsync(connection, runId.Value, cancellationToken))
        {
            return;
        }

        var activity = await UpsertContactOnlyWhatsappActivityAsync(
            connection,
            accountId,
            contactId.Value,
            userId,
            companyId.Value,
            conversationId.Value,
            opportunityEvent,
            result,
            cancellationToken);
        await PersistStructuredAnalysisAndScorecardAsync(
            connection, runId, activity.Id, companyId, userId, result, cancellationToken);
        await InsertAgentSuggestionsAsync(connection, companyId, contactId, conversationId, runId, null, result, cancellationToken);
        await CompleteQueuedRunAsync(connection, runId, conversationId, result.ConversationSummary, cancellationToken);
    }

    private static async Task<bool> IsRunCompletedAsync(NpgsqlConnection connection, Guid runId, CancellationToken cancellationToken)
    {
        const string sql = "select coalesce((select status = 'completed' from whatsapp_conversation_analysis_runs where id = @runId), false)";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("runId", runId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static void AppendAnalysisSections(List<string> lines, WhatsappConversationAnalysisResult result)
    {
        lines.Add("Resumo:");
        lines.Add(result.ConversationSummary);
        if (!string.IsNullOrWhiteSpace(result.CommercialObservations))
        {
            lines.Add("");
            lines.Add("Observações comerciais:");
            lines.Add(result.CommercialObservations);
        }
        if (result.NextSteps is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("Próximos passos:");
            lines.AddRange(result.NextSteps.Select(item => "- " + item));
        }
        if (result.Insights is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("Outros insights:");
            lines.AddRange(result.Insights.Select(item => "- " + item));
        }
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

    private static async Task<DailyActivityUpsertResult> UpsertSkoposAnalysisActivityAsync(
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
        var eventMarker = $"[agent_skopos_event:{context.TriggerEvent.EventId}]";
        var marker = conversationId is null
            ? $"[whatsapp_event_id:{context.TriggerEvent.EventId}]\n{eventMarker}"
            : $"[whatsapp_conversation_id:{conversationId}]\n{eventMarker}";
        var notesBlock = BuildActivityNotesBlock(context, result, marker);
        var completedNotes = Truncate(result.ConversationSummary, 2000);

        return await UpsertDailyAnalysisActivityAsync(
            connection, opportunityId, accountId, contactId, userId, companyId, activityDate,
            notesBlock, completedNotes, eventMarker, cancellationToken);
    }

    private static async Task<DailyActivityUpsertResult> UpsertContactOnlyWhatsappActivityAsync(
        NpgsqlConnection connection,
        Guid? accountId,
        Guid contactId,
        Guid? userId,
        Guid companyId,
        Guid conversationId,
        OpportunityEvent opportunityEvent,
        WhatsappConversationAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var activityDate = (GetDateTime(opportunityEvent, "latestMessageAt") ?? opportunityEvent.OccurredAt).ToUniversalTime();
        var notesLines = new List<string>
        {
            "Atividade criada automaticamente pelo Agent Skopos após análise da conversa do WhatsApp.",
            $"[whatsapp_conversation_id:{conversationId}]",
            $"[agent_skopos_event:{opportunityEvent.EventId}]",
            ""
        };
        AppendAnalysisSections(notesLines, result);
        return await UpsertDailyAnalysisActivityAsync(
            connection, null, accountId, contactId, userId, companyId, activityDate,
            string.Join("\n", notesLines), Truncate(result.ConversationSummary, 2000),
            $"[agent_skopos_event:{opportunityEvent.EventId}]", cancellationToken);
    }

    private static async Task<DailyActivityUpsertResult> UpsertDailyAnalysisActivityAsync(
        NpgsqlConnection connection,
        Guid? opportunityId,
        Guid? accountId,
        Guid? contactId,
        Guid? userId,
        Guid? companyId,
        DateTime activityDate,
        string notesBlock,
        string completedNotes,
        string eventMarker,
        CancellationToken cancellationToken)
    {
        var activityId = Guid.NewGuid();
        var lockKey = $"whatsapp-analysis:{companyId}:{contactId?.ToString() ?? opportunityId?.ToString()}";
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", lockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            with settings as (
                select coalesce(
                    (select time_zone_id from daily_checkout_settings where company_id = @companyId limit 1),
                    'America/Sao_Paulo') as time_zone_id
            ), existing as (
                select activity.id
                from activities activity
                cross join settings
                where activity.company_id is not distinct from @companyId
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
                set opportunity_id = coalesce(activity.opportunity_id, @opportunityId),
                    account_id = coalesce(activity.account_id, @accountId),
                    contact_id = coalesce(activity.contact_id, @contactId),
                    owner_user_id = coalesce(activity.owner_user_id, @userId),
                    date_at = greatest(activity.date_at, @dateAt),
                    notes = case
                        when activity.notes like @eventMarker then activity.notes
                        else concat_ws(E'\n\n---\n\n', nullif(activity.notes, ''), @notes)
                    end,
                    completion_notes = @completedNotes,
                    completed_notes = @completedNotes,
                    updated_at = now()
                where activity.id = (select id from existing)
                returning activity.id
            ), inserted as (
                insert into activities
                    (id, opportunity_id, account_id, contact_id, owner_user_id, title, activity_type, channel,
                     status, date_at, notes, completion_notes, completed_notes, company_id, created_at, updated_at)
                select
                    @id, @opportunityId, @accountId, @contactId, @userId, @title, @activityType, @channel,
                    'done', @dateAt, @notes, @completedNotes, @completedNotes, @companyId, now(), now()
                where not exists (select 1 from changed)
                returning id
            )
            select id, true as was_created from inserted
            union all
            select id, false as was_created from changed
            limit 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", activityId);
        command.Parameters.Add("opportunityId", NpgsqlDbType.Uuid).Value = opportunityId is null ? DBNull.Value : opportunityId.Value;
        command.Parameters.Add("accountId", NpgsqlDbType.Uuid).Value = accountId is null ? DBNull.Value : accountId.Value;
        command.Parameters.Add("contactId", NpgsqlDbType.Uuid).Value = contactId is null ? DBNull.Value : contactId.Value;
        command.Parameters.Add("userId", NpgsqlDbType.Uuid).Value = userId is null ? DBNull.Value : userId.Value;
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = companyId is null ? DBNull.Value : companyId.Value;
        command.Parameters.AddWithValue("title", "Conversa WhatsApp analisada pelo Agent Skopos");
        command.Parameters.AddWithValue("activityType", SkoposActivityType);
        command.Parameters.AddWithValue("channel", WhatsappChannel);
        command.Parameters.AddWithValue("dateAt", activityDate);
        command.Parameters.AddWithValue("notes", notesBlock);
        command.Parameters.AddWithValue("eventMarker", $"%{eventMarker}%");
        command.Parameters.AddWithValue("completedNotes", completedNotes);
        Guid persistedActivityId;
        bool wasInserted;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Não foi possível consolidar a atividade diária do WhatsApp.");
            persistedActivityId = reader.GetGuid(0);
            wasInserted = reader.GetBoolean(1);
        }

        if (opportunityId is not null)
        {
            await using var opportunityCommand = new NpgsqlCommand(
                "update opportunities set last_activity_at = greatest(coalesce(last_activity_at, @dateAt), @dateAt), updated_at = now() where id = @opportunityId;",
                connection,
                transaction);
            opportunityCommand.Parameters.AddWithValue("opportunityId", opportunityId.Value);
            opportunityCommand.Parameters.AddWithValue("dateAt", activityDate);
            await opportunityCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DailyActivityUpsertResult(persistedActivityId, wasInserted);
    }

    private static async Task PersistStructuredAnalysisAndScorecardAsync(
        NpgsqlConnection connection,
        Guid? runId,
        Guid activityId,
        Guid? companyId,
        Guid? evaluatedUserId,
        WhatsappConversationAnalysisResult result,
        CancellationToken cancellationToken)
    {
        if (runId is null || companyId is null) return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string analysisSql = """
            update whatsapp_conversation_analysis_runs
            set activity_id = @activityId,
                analysis_json = @analysisJson,
                confidence_score = @confidenceScore,
                model = @model,
                prompt_fingerprint = @promptFingerprint,
                updated_at = now()
            where id = @runId and company_id = @companyId
            """;
        await using (var command = new NpgsqlCommand(analysisSql, connection, transaction))
        {
            command.Parameters.AddWithValue("runId", runId.Value);
            command.Parameters.AddWithValue("companyId", companyId.Value);
            command.Parameters.AddWithValue("activityId", activityId);
            command.Parameters.Add("analysisJson", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(result, SerializerOptions);
            command.Parameters.AddWithValue("confidenceScore", Math.Clamp(result.ConfidenceScore, 0, 100));
            command.Parameters.Add("model", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(result.GenerationModel) ? DBNull.Value : result.GenerationModel;
            command.Parameters.Add("promptFingerprint", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(result.PromptFingerprint) ? DBNull.Value : result.PromptFingerprint;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("A execução da análise do WhatsApp não pertence à empresa informada.");
        }

        if (result.Scorecard is null
            || !Guid.TryParse(result.Scorecard.TemplateId, out var templateId)
            || !Guid.TryParse(result.Scorecard.TemplateKey, out var templateKey))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using (var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("key", $"whatsapp-scorecard:{companyId}:{activityId}");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string existingSql = """
            select exists (
                select 1 from conversation_scorecards
                where company_id = @companyId and whatsapp_analysis_run_id = @runId
            )
            """;
        await using (var command = new NpgsqlCommand(existingSql, connection, transaction))
        {
            command.Parameters.AddWithValue("companyId", companyId.Value);
            command.Parameters.AddWithValue("runId", runId.Value);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using (var command = new NpgsqlCommand("""
            update conversation_scorecards
            set is_current = false, updated_at = now()
            where company_id = @companyId
              and activity_id = @activityId
              and source_kind = 'whatsapp_conversation'
              and is_current
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("companyId", companyId.Value);
            command.Parameters.AddWithValue("activityId", activityId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var coveredItems = result.Scorecard.Items.Where(item => item.ConfidenceScore > 0 && item.Evidence.Count > 0).ToArray();
        var coveredWeight = coveredItems.Sum(item => item.Weight);
        var overallScore = coveredWeight <= 0
            ? 0m
            : Math.Round(coveredItems.Sum(item => item.Score * item.Weight) / coveredWeight, 2);
        var overallConfidence = coveredWeight <= 0
            ? 0
            : Math.Clamp((int)Math.Round(coveredItems.Sum(item => item.ConfidenceScore * item.Weight) / coveredWeight), 0, 100);
        var scorecardId = Guid.NewGuid();

        const string scorecardSql = """
            insert into conversation_scorecards
                (id, company_id, analysis_result_id, recording_id, whatsapp_analysis_run_id, activity_id,
                 source_kind, is_current, template_id, template_key, template_version,
                 evaluated_user_id, group_id, ai_score, status, confidence_score, model, prompt_fingerprint)
            select
                @id, @companyId, null, null, @runId, @activityId,
                'whatsapp_conversation', true, @templateId, @templateKey, @templateVersion,
                @evaluatedUserId, owner.group_id, @aiScore, 'generated', @confidenceScore, @model, @promptFingerprint
            from (select 1) seed
            left join users owner on owner.id = @evaluatedUserId and owner.company_id = @companyId
            """;
        await using (var command = new NpgsqlCommand(scorecardSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", scorecardId);
            command.Parameters.AddWithValue("companyId", companyId.Value);
            command.Parameters.AddWithValue("runId", runId.Value);
            command.Parameters.AddWithValue("activityId", activityId);
            command.Parameters.AddWithValue("templateId", templateId);
            command.Parameters.AddWithValue("templateKey", templateKey);
            command.Parameters.AddWithValue("templateVersion", result.Scorecard.TemplateVersion);
            command.Parameters.Add("evaluatedUserId", NpgsqlDbType.Uuid).Value = evaluatedUserId ?? (object)DBNull.Value;
            command.Parameters.AddWithValue("aiScore", overallScore);
            command.Parameters.AddWithValue("confidenceScore", overallConfidence);
            command.Parameters.Add("model", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(result.GenerationModel) ? DBNull.Value : result.GenerationModel;
            command.Parameters.Add("promptFingerprint", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(result.PromptFingerprint) ? DBNull.Value : result.PromptFingerprint;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string itemSql = """
            insert into conversation_scorecard_items
                (id, scorecard_id, criterion_id, criterion_key, criterion_title, weight,
                 ai_score, confidence_score, justification, recommendation, evidence_json)
            values
                (gen_random_uuid(), @scorecardId, @criterionId, @criterionKey, @criterionTitle, @weight,
                 @score, @confidenceScore, @justification, @recommendation, @evidenceJson)
            """;
        foreach (var item in result.Scorecard.Items)
        {
            if (!Guid.TryParse(item.CriterionId, out var criterionId)) continue;
            await using var command = new NpgsqlCommand(itemSql, connection, transaction);
            command.Parameters.AddWithValue("scorecardId", scorecardId);
            command.Parameters.AddWithValue("criterionId", criterionId);
            command.Parameters.AddWithValue("criterionKey", item.CriterionKey);
            command.Parameters.AddWithValue("criterionTitle", item.CriterionTitle);
            command.Parameters.AddWithValue("weight", item.Weight);
            command.Parameters.AddWithValue("score", Math.Clamp(item.Score, 0, 100));
            command.Parameters.AddWithValue("confidenceScore", Math.Clamp(item.ConfidenceScore, 0, 100));
            command.Parameters.AddWithValue("justification", item.Justification);
            command.Parameters.Add("recommendation", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(item.Recommendation) ? DBNull.Value : item.Recommendation;
            command.Parameters.Add("evidenceJson", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(item.Evidence, SerializerOptions);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
            ""
        };
        AppendAnalysisSections(lines, result);

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

    private static async Task InsertAgentSuggestionsAsync(
        NpgsqlConnection connection,
        Guid? companyId,
        Guid? contactId,
        Guid? conversationId,
        Guid? runId,
        Guid? currentOpportunityId,
        WhatsappConversationAnalysisResult result,
        CancellationToken cancellationToken)
    {
        if (companyId is null || contactId is null || runId is null)
        {
            return;
        }

        if (result.ShouldCreateActivity && !string.IsNullOrWhiteSpace(result.ActivityTitle))
        {
            var description = string.IsNullOrWhiteSpace(result.ActivityNotes) ? result.ActivityTitle : result.ActivityNotes;
            var payload = JsonSerializer.Serialize(new
            {
                activityType = "follow-up",
                channel = "whatsapp",
                dueAt = result.ActivityDueAt,
                notes = result.ActivityNotes,
                semanticIntentKey = result.ActivityIntentKey
            }, SerializerOptions);
            await InsertAgentSuggestionAsync(connection, companyId.Value, contactId.Value, conversationId, runId.Value,
                "activity", result.ActivityTitle!, description!, result.ActivityDueAt, payload,
                result.ActivityMatchingSuggestionId, result.ActivityIntentKey, result, cancellationToken);
        }

        if (result.ShouldCreateOpportunity && !string.IsNullOrWhiteSpace(result.OpportunityTitle)
            && !await HasAgentMatchedOpenOpportunityAsync(
                connection, contactId.Value, currentOpportunityId, result.MatchingOpenOpportunityId, cancellationToken))
        {
            var description = string.IsNullOrWhiteSpace(result.OpportunityDescription)
                ? result.OpportunityTitle
                : result.OpportunityDescription;
            var payload = JsonSerializer.Serialize(new
            {
                origin = "WhatsApp",
                description = result.OpportunityDescription,
                semanticIntentKey = result.OpportunityIntentKey
            }, SerializerOptions);
            await InsertAgentSuggestionAsync(connection, companyId.Value, contactId.Value, conversationId, runId.Value,
                "opportunity", result.OpportunityTitle!, description!, null, payload,
                result.OpportunityMatchingSuggestionId, result.OpportunityIntentKey, result, cancellationToken);
        }
    }

    private static async Task InsertAgentSuggestionAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid contactId,
        Guid? conversationId,
        Guid runId,
        string type,
        string title,
        string description,
        DateTime? dueAt,
        string payload,
        string? matchingSuggestionId,
        string? semanticIntentKey,
        WhatsappConversationAnalysisResult analysisResult,
        CancellationToken cancellationToken)
    {
        var suggestionLockKey = $"whatsapp-suggestion:{companyId}:{contactId}:{type}:{semanticIntentKey ?? matchingSuggestionId ?? runId.ToString()}";
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", suggestionLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            with existing as (
                select id
                from ai_agent_suggestions
                where company_id = @companyId
                  and agent_key = 'whatsapp-conversation-analysis'
                  and suggestion_type = @type
                  and contact_id = @contactId
                  and status in ('pending', 'rejected')
                  and (
                    id = @matchingSuggestionId
                    or (@semanticIntentKey is not null and payload ->> 'semanticIntentKey' = @semanticIntentKey)
                  )
                order by case when status = 'pending' then 0 else 1 end, updated_at desc
                limit 1
            ), updated as (
                update ai_agent_suggestions suggestion
                set conversation_id = @conversationId,
                    run_id = @runId,
                    title = @title,
                    description = @description,
                    suggested_due_at = @dueAt,
                    payload = @payload,
                    generation_model = @generationModel,
                    prompt_fingerprint = @promptFingerprint,
                    confidence_score = @confidenceScore,
                    generation_reasons = @generationReasons,
                    updated_at = now()
                where suggestion.id = (select id from existing)
                returning suggestion.id
            )
            insert into ai_agent_suggestions
                (id, company_id, agent_key, suggestion_type, status, contact_id, conversation_id, run_id,
                 title, description, suggested_due_at, payload, generation_model, prompt_fingerprint,
                 confidence_score, generation_reasons, created_at, updated_at)
            select
                @id, @companyId, 'whatsapp-conversation-analysis', @type, 'pending', @contactId, @conversationId, @runId,
                @title, @description, @dueAt, @payload, @generationModel, @promptFingerprint,
                @confidenceScore, @generationReasons, now(), now()
            where not exists (select 1 from updated)
            on conflict (run_id, suggestion_type) where run_id is not null do nothing;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("contactId", contactId);
        command.Parameters.Add("conversationId", NpgsqlDbType.Uuid).Value = conversationId is null ? DBNull.Value : conversationId.Value;
        command.Parameters.AddWithValue("runId", runId);
        command.Parameters.Add("matchingSuggestionId", NpgsqlDbType.Uuid).Value =
            Guid.TryParse(matchingSuggestionId, out var parsedSuggestionId) ? parsedSuggestionId : DBNull.Value;
        command.Parameters.Add("semanticIntentKey", NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(semanticIntentKey) ? DBNull.Value : semanticIntentKey;
        command.Parameters.AddWithValue("title", Truncate(title, 300));
        command.Parameters.AddWithValue("description", Truncate(description, 3000));
        command.Parameters.Add("dueAt", NpgsqlDbType.TimestampTz).Value = dueAt is null ? DBNull.Value : dueAt.Value;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.Add("generationModel", NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(analysisResult.GenerationModel) ? DBNull.Value : analysisResult.GenerationModel;
        command.Parameters.Add("promptFingerprint", NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(analysisResult.PromptFingerprint) ? DBNull.Value : analysisResult.PromptFingerprint;
        command.Parameters.AddWithValue("confidenceScore", Math.Clamp(analysisResult.ConfidenceScore, 0, 100));
        command.Parameters.Add("generationReasons", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(analysisResult.Reasons, SerializerOptions);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> HasAgentMatchedOpenOpportunityAsync(
        NpgsqlConnection connection,
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
                    or
                    exists (
                      select 1 from opportunity_contacts link
                      where link.opportunity_id = opportunity.id
                        and link.contact_id = @contactId
                    )
                    or opportunity.account_id = (select account_id from contacts where id = @contactId)
                  )
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("contactId", contactId);
        command.Parameters.AddWithValue("matchingOpenOpportunityId", parsedOpportunityId);
        command.Parameters.Add("currentOpportunityId", NpgsqlDbType.Uuid).Value =
            currentOpportunityId is null ? DBNull.Value : currentOpportunityId.Value;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
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
            result.ShouldCreateOpportunity,
            result.ConversationSummary,
            result.CommercialObservations,
            result.NextSteps,
            result.Insights,
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
            update whatsapp_conversation_analysis_runs previous
            set status = 'superseded',
                updated_at = now()
            from whatsapp_conversation_analysis_runs current_run
            where current_run.id = @runId
              and previous.id <> current_run.id
              and previous.conversation_id = current_run.conversation_id
              and previous.last_message_id = current_run.last_message_id
              and previous.status = 'completed';

            update whatsapp_conversation_analysis_runs
            set status = 'completed',
                summary = @summary,
                error = null,
                updated_at = now()
            where id = @runId;

            update whatsapp_conversations
            set summary = @summary,
                last_analyzed_message_at = greatest(
                    coalesce(last_analyzed_message_at, '-infinity'::timestamptz),
                    coalesce((select window_end_at from whatsapp_conversation_analysis_runs where id = @runId), last_analyzed_message_at)),
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

    private sealed record DailyActivityUpsertResult(Guid Id, bool WasCreated);
}
