using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmAi.Application;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;

namespace CrmAi.Infrastructure.Persistence;

public sealed class SuggestionCompletionVerificationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SuggestionCompletionVerificationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<SuggestionCompletionVerificationProcessor>();
                var processed = await processor.ProcessNextAsync(stoppingToken);
                await processor.PublishPendingNotificationsAsync(stoppingToken);
                await processor.PublishPendingResponseNotificationsAsync(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Suggestion completion verification worker failed.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}

public sealed class SuggestionCompletionVerificationProcessor(
    NpgsqlDataSource dataSource,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    IOpenAiSuggestionCompletionVerificationClient client,
    IOptions<RabbitMqOptions> rabbitOptions,
    ILogger<SuggestionCompletionVerificationProcessor> logger)
{
    internal const string AgentKey = "suggestion-completion-verification";
    internal const string NotificationEventKey = "activity_suggestion_unfulfilled";
    internal const string ResponseNotificationEventKey = "activity_suggestion_response_pending";
    internal const int ConfidenceThreshold = 80;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ClaimedSuggestion(
        Guid Id,
        Guid CompanyId,
        Guid ContactId,
        Guid? ConversationId,
        string SuggestionType,
        string Title,
        string Description,
        DateTime CreatedAt,
        DateTime SuggestedDueAt,
        string Payload,
        string PreviousVerificationStatus,
        string? PreviousEvidenceFingerprint,
        int AttemptCount);

    private sealed record NotificationCandidate(
        Guid Id,
        Guid CompanyId,
        Guid UserId,
        Guid TargetId,
        string TargetType,
        string ContactName,
        string SuggestionTitle,
        string SuggestionDescription);
    private sealed record NotificationBatch(
        Guid Id,
        Guid CompanyId,
        Guid UserId,
        string Title,
        string Message,
        DateTime CreatedAt,
        string DedupeKey,
        string Href,
        string EntityType,
        Guid EntityId,
        string EventKey,
        string Severity);
    private sealed record ResponseNotificationCandidate(
        Guid SuggestionId,
        Guid CompanyId,
        Guid UserId,
        string ContactName,
        string SuggestionTitle,
        DateTime ResponseRequiredAt,
        string Href);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var suggestion = await ClaimAsync(cancellationToken);
        if (suggestion is null) return false;

        IReadOnlyCollection<SuggestionCompletionEvidence> evidence = [];
        string? fingerprint = null;
        try
        {
            evidence = await LoadEvidenceAsync(suggestion, cancellationToken);
            fingerprint = Fingerprint(evidence);
            if (suggestion.PreviousVerificationStatus == "unfulfilled"
                && string.Equals(suggestion.PreviousEvidenceFingerprint, fingerprint, StringComparison.Ordinal))
            {
                await RestoreUnchangedPriorityAsync(suggestion.Id, fingerprint, cancellationToken);
                return true;
            }

            SuggestionCompletionVerificationResult result;
            if (!evidence.Any(item => !item.BeforeSuggestion))
            {
                result = new("unfulfilled", 100, "Nenhum registro posterior à sugestão foi encontrado.", []);
            }
            else
            {
                var settings = await settingsRepository.GetAsync(AgentKey, suggestion.CompanyId.ToString(), cancellationToken);
                if (!settings.IsActive) throw new InvalidOperationException("O agente suggestion-completion-verification está inativo.");
                using var payloadDocument = JsonDocument.Parse(suggestion.Payload);
                result = await client.AnalyzeAsync(
                    settings,
                    new SuggestionCompletionVerificationInput(
                        suggestion.Id.ToString(),
                        suggestion.SuggestionType,
                        suggestion.Title,
                        suggestion.Description,
                        suggestion.CreatedAt,
                        suggestion.SuggestedDueAt,
                        payloadDocument.RootElement.Clone(),
                        evidence),
                    new AiAgentInvocationContext(
                        "suggestions.completion-verification",
                        suggestion.CompanyId.ToString(),
                        WhatsappConversationId: suggestion.ConversationId?.ToString(),
                        ContactId: suggestion.ContactId.ToString(),
                        ContextEntityKeys: settings.ContextEntityKeys,
                        Metadata: new Dictionary<string, object?> { ["suggestionId"] = suggestion.Id }),
                    cancellationToken);
            }

            var normalized = NormalizeResult(result);
            await CompleteAsync(suggestion, normalized, evidence, fingerprint, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync(suggestion, exception, evidence, fingerprint, cancellationToken);
        }
        return true;
    }

    private async Task<ClaimedSuggestion?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            with candidate as (
                select id, verification_status, evidence_fingerprint
                from ai_agent_suggestions
                where status = 'pending'
                  and suggested_due_at is not null
                  and suggested_due_at <= now() - interval '5 minutes'
                  and (
                    verification_status <> 'processing'
                    or updated_at < now() - interval '15 minutes'
                  )
                  and coalesce(next_verification_at, suggested_due_at + interval '5 minutes') <= now()
                order by case when priority_at is null then 0 else 1 end,
                         suggested_due_at,
                         created_at
                for update skip locked
                limit 1
            )
            update ai_agent_suggestions suggestion
            set verification_status = 'processing',
                verification_attempt_count = suggestion.verification_attempt_count + 1,
                next_verification_at = now() + interval '15 minutes',
                updated_at = now()
            from candidate
            where suggestion.id = candidate.id
            returning suggestion.id, suggestion.company_id, suggestion.contact_id, suggestion.conversation_id,
                      suggestion.suggestion_type, suggestion.title, suggestion.description,
                      suggestion.created_at, suggestion.suggested_due_at, suggestion.payload::text,
                      candidate.verification_status as previous_verification_status,
                      candidate.evidence_fingerprint as previous_evidence_fingerprint,
                      suggestion.verification_attempt_count;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        ClaimedSuggestion? result = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            result = new(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetDateTime(7).ToUniversalTime(),
                reader.GetDateTime(8).ToUniversalTime(), reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetInt32(12));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<IReadOnlyCollection<SuggestionCompletionEvidence>> LoadEvidenceAsync(
        ClaimedSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            with contact_scope as (
                select id, account_id from contacts where id = @contactId and company_id = @companyId
            ), related_opportunities as (
                select distinct opportunity.id
                from opportunities opportunity
                left join opportunity_contacts relation on relation.opportunity_id = opportunity.id
                cross join contact_scope contact
                where opportunity.company_id = @companyId
                  and (relation.contact_id = @contactId or opportunity.account_id = contact.account_id)
            ), evidence as (
                select 'activity:' || activity.id::text as id,
                       'activity' as type,
                       greatest(activity.updated_at, activity.created_at) as occurred_at,
                       concat_ws(' | ', activity.title, activity.activity_type, activity.channel, activity.status,
                           nullif(activity.completed_notes, ''), nullif(activity.notes, '')) as summary
                from activities activity
                where activity.company_id = @companyId
                  and (activity.contact_id = @contactId or activity.opportunity_id in (select id from related_opportunities))
                  and greatest(activity.updated_at, activity.created_at) >= @windowStart

                union all
                select 'whatsapp:' || message.id::text, 'whatsapp_message', message.message_at,
                       concat_ws(' | ', message.direction, message.message_type, nullif(message.text, ''))
                from whatsapp_messages message
                inner join whatsapp_conversations conversation on conversation.id = message.conversation_id
                where conversation.company_id = @companyId and conversation.contact_id = @contactId
                  and message.message_at >= @windowStart

                union all
                select 'instagram:' || message.id::text, 'instagram_message', message.message_at,
                       concat_ws(' | ', message.direction, message.message_type, nullif(message.text, ''))
                from instagram_messages message
                inner join instagram_conversations conversation on conversation.id = message.conversation_id
                where conversation.company_id = @companyId and conversation.contact_id = @contactId
                  and message.message_at >= @windowStart

                union all
                select 'note:' || note.id::text, 'note', note.created_at, note.text
                from notes note
                cross join contact_scope contact
                where note.company_id = @companyId
                  and (note.contact_id = @contactId or note.account_id = contact.account_id
                       or note.opportunity_id in (select id from related_opportunities))
                  and note.created_at >= @windowStart

                union all
                select 'opportunity:' || opportunity.id::text, 'opportunity',
                       greatest(opportunity.updated_at, opportunity.created_at),
                       concat_ws(' | ', opportunity.name, opportunity.status)
                from opportunities opportunity
                where opportunity.id in (select id from related_opportunities)
                  and greatest(opportunity.updated_at, opportunity.created_at) >= @windowStart

                union all
                select 'history:' || history.id::text, 'opportunity_history', history.created_at, history.event
                from opportunity_history history
                where history.company_id = @companyId
                  and history.opportunity_id in (select id from related_opportunities)
                  and history.created_at >= @windowStart

                union all
                select 'meeting:' || recording.id::text, 'meeting_recording',
                       coalesce(recording.transcribed_at, recording.updated_at, recording.created_at),
                       concat_ws(' | ', nullif(recording.summary, ''), nullif(recording.transcript, ''))
                from meeting_audio_recordings recording
                left join activities activity on activity.id = recording.activity_id
                where recording.company_id = @companyId
                  and (activity.contact_id = @contactId or recording.opportunity_id in (select id from related_opportunities))
                  and coalesce(recording.transcribed_at, recording.updated_at, recording.created_at) >= @windowStart
            )
            select id, type, occurred_at, left(summary, 1200)
            from evidence
            where nullif(btrim(summary), '') is not null
            order by occurred_at desc
            limit 120;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", suggestion.CompanyId);
        command.Parameters.AddWithValue("contactId", suggestion.ContactId);
        command.Parameters.AddWithValue("windowStart", suggestion.CreatedAt.AddHours(-24));
        var rows = new List<SuggestionCompletionEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var occurredAt = reader.GetDateTime(2).ToUniversalTime();
            rows.Add(new(reader.GetString(0), reader.GetString(1), occurredAt, occurredAt < suggestion.CreatedAt, reader.GetString(3)));
        }
        return rows;
    }

    private static SuggestionCompletionVerificationResult NormalizeResult(SuggestionCompletionVerificationResult result)
    {
        var normalized = result.Result.Trim().ToLowerInvariant();
        if (normalized is not ("fulfilled" or "unfulfilled" or "inconclusive") || result.Confidence < ConfidenceThreshold)
        {
            normalized = "inconclusive";
        }
        return result with
        {
            Result = normalized,
            Confidence = Math.Clamp(result.Confidence, 0, 100),
            Reason = Truncate(result.Reason.Trim(), 2000),
            EvidenceIds = result.EvidenceIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray()
        };
    }

    private async Task CompleteAsync(
        ClaimedSuggestion suggestion,
        SuggestionCompletionVerificationResult result,
        IReadOnlyCollection<SuggestionCompletionEvidence> evidence,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var evidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
        var matchedEvidenceJson = JsonSerializer.Serialize(evidence.Where(item => result.EvidenceIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)), JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string insertSql = """
            insert into ai_agent_suggestion_verifications
                (id, company_id, suggestion_id, result, confidence, reason, evidence,
                 evidence_fingerprint, model, prompt_fingerprint, created_at)
            values
                (gen_random_uuid(), @companyId, @suggestionId, @result, @confidence, @reason,
                 @matchedEvidence::jsonb, @fingerprint, @model, null, now());
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("companyId", suggestion.CompanyId);
            insert.Parameters.AddWithValue("suggestionId", suggestion.Id);
            insert.Parameters.AddWithValue("result", result.Result);
            insert.Parameters.AddWithValue("confidence", result.Confidence);
            insert.Parameters.AddWithValue("reason", result.Reason);
            insert.Parameters.Add("matchedEvidence", NpgsqlDbType.Jsonb).Value = matchedEvidenceJson;
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("model", AgentKey);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateSql = """
            update ai_agent_suggestions
            set status = case when @result = 'fulfilled' then 'fulfilled' else status end,
                resolved_at = case when @result = 'fulfilled' then now() else resolved_at end,
                verification_status = @result,
                last_verified_at = now(),
                next_verification_at = case
                    when @result = 'fulfilled' then null
                    when @result = 'unfulfilled' then now() + interval '15 minutes'
                    else now() + interval '5 minutes' * least(12, verification_attempt_count)
                end,
                priority_at = case
                    when @result = 'fulfilled' then null
                    when @result = 'unfulfilled' then coalesce(priority_at, now())
                    else priority_at
                end,
                priority_notified_at = case when @result = 'fulfilled' then null else priority_notified_at end,
                evidence_fingerprint = @fingerprint,
                verification_confidence = @confidence,
                verification_reason = @reason,
                verification_model = @model,
                verification_evidence = @evidence::jsonb,
                updated_at = now()
            where id = @suggestionId and status = 'pending';
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            update.Parameters.AddWithValue("suggestionId", suggestion.Id);
            update.Parameters.AddWithValue("result", result.Result);
            update.Parameters.AddWithValue("fingerprint", fingerprint);
            update.Parameters.AddWithValue("confidence", result.Confidence);
            update.Parameters.AddWithValue("reason", result.Reason);
            update.Parameters.AddWithValue("model", AgentKey);
            update.Parameters.Add("evidence", NpgsqlDbType.Jsonb).Value = evidenceJson;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Suggestion verification completed. SuggestionId={SuggestionId} Result={Result} Confidence={Confidence}", suggestion.Id, result.Result, result.Confidence);
    }

    private async Task RestoreUnchangedPriorityAsync(Guid suggestionId, string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "update ai_agent_suggestions set verification_status='unfulfilled', evidence_fingerprint=@fingerprint, last_verified_at=now(), next_verification_at=now()+interval '15 minutes', updated_at=now() where id=@id and status='pending';",
            connection);
        command.Parameters.AddWithValue("id", suggestionId);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FailAsync(
        ClaimedSuggestion suggestion,
        Exception exception,
        IReadOnlyCollection<SuggestionCompletionEvidence> evidence,
        string? fingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var reason = Truncate(exception.Message, 2000);
        await using (var history = new NpgsqlCommand(
            "insert into ai_agent_suggestion_verifications(id,company_id,suggestion_id,result,reason,evidence,evidence_fingerprint,model,created_at) values(gen_random_uuid(),@companyId,@suggestionId,'failed',@reason,@evidence::jsonb,@fingerprint,@model,now());",
            connection, transaction))
        {
            history.Parameters.AddWithValue("companyId", suggestion.CompanyId);
            history.Parameters.AddWithValue("suggestionId", suggestion.Id);
            history.Parameters.AddWithValue("reason", reason);
            history.Parameters.Add("evidence", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(evidence, JsonOptions);
            history.Parameters.Add("fingerprint", NpgsqlDbType.Text).Value = fingerprint is null ? DBNull.Value : fingerprint;
            history.Parameters.AddWithValue("model", AgentKey);
            await history.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = new NpgsqlCommand(
            "update ai_agent_suggestions set verification_status='failed', last_verified_at=now(), next_verification_at=now()+interval '5 minutes'*least(12,verification_attempt_count), evidence_fingerprint=@fingerprint, verification_reason=@reason, updated_at=now() where id=@id and status='pending';",
            connection, transaction))
        {
            update.Parameters.AddWithValue("id", suggestion.Id);
            update.Parameters.AddWithValue("reason", reason);
            update.Parameters.Add("fingerprint", NpgsqlDbType.Text).Value = fingerprint is null ? DBNull.Value : fingerprint;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogError(exception, "Suggestion verification failed. SuggestionId={SuggestionId}", suggestion.Id);
    }

    public async Task PublishPendingNotificationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("select pg_try_advisory_xact_lock(hashtext('suggestion-completion-notifications'));", connection, transaction))
        {
            if (!Convert.ToBoolean(await lockCommand.ExecuteScalarAsync(cancellationToken)))
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
        }

        const string candidatesSql = """
            select suggestion.id, suggestion.company_id,
                   coalesce(responsible.user_id, opportunity.owner_user_id, contact.owner_user_id) as user_id,
                   coalesce(opportunity.id, suggestion.contact_id) as target_id,
                   case when opportunity.id is null then 'contact' else 'opportunity' end as target_type,
                   coalesce(contact.name, 'Contato'),
                   coalesce(suggestion.title, 'Sugestão'),
                   coalesce(suggestion.description, '')
            from ai_agent_suggestions suggestion
            inner join contacts contact on contact.id = suggestion.contact_id and contact.company_id = suggestion.company_id
            left join lateral (
                select assigned.user_id
                from contact_responsibles assigned
                inner join users assigned_user on assigned_user.id = assigned.user_id and assigned_user.is_active = true
                where assigned.contact_id = contact.id
                order by assigned.is_primary desc, assigned.created_at
                limit 1
            ) responsible on true
            left join lateral (
                select candidate.id, candidate.owner_user_id
                from opportunity_contacts relation
                inner join opportunities candidate on candidate.id = relation.opportunity_id
                where relation.contact_id = contact.id and candidate.company_id = suggestion.company_id and candidate.status = 'active'
                order by candidate.updated_at desc
                limit 1
            ) opportunity on true
            where suggestion.status = 'pending'
              and suggestion.priority_at is not null
              and suggestion.priority_notified_at is null
              and coalesce(responsible.user_id, opportunity.owner_user_id, contact.owner_user_id) is not null
            order by suggestion.priority_at
            for update of suggestion skip locked
            limit 500;
            """;
        var candidates = new List<NotificationCandidate>();
        await using (var command = new NpgsqlCommand(candidatesSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }
        if (candidates.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var notifications = new List<NotificationBatch>();
        foreach (var group in candidates.GroupBy(item => new { item.CompanyId, item.UserId, item.TargetId, item.TargetType }))
        {
            var ids = group.Select(item => item.Id).Order().ToArray();
            var dedupeKey = $"suggestion-unfulfilled:{group.Key.UserId}:{Fingerprint(ids.Select(id => id.ToString()))}";
            var title = "Ações sugeridas aguardando registro";
            var message = BuildUnfulfilledNotificationMessage(group.ToArray());
            var href = group.Key.TargetType == "opportunity"
                ? $"/crm/opportunities/{group.Key.TargetId}"
                : $"/crm/contacts/{group.Key.TargetId}";
            var notificationId = Guid.NewGuid();
            var createdAt = DateTime.UtcNow;
            const string insertSql = """
                insert into notifications(id,company_id,user_id,event_key,title,message,severity,href,entity_type,entity_id,channels_json,dedupe_key,created_at,updated_at)
                values(@id,@companyId,@userId,@eventKey,@title,@message,'danger',@href,@entityType,@entityId,
                       '[{"channel":"system","enabled":true,"status":"sent"},{"channel":"toast","enabled":true,"status":"sent"},{"channel":"browser","enabled":true,"status":"sent"},{"channel":"email","enabled":false,"status":"disabled"},{"channel":"whatsapp","enabled":false,"status":"disabled"}]'::jsonb,
                       @dedupeKey,@createdAt,@createdAt)
                on conflict do nothing
                returning id;
                """;
            await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
            insert.Parameters.AddWithValue("id", notificationId);
            insert.Parameters.AddWithValue("companyId", group.Key.CompanyId);
            insert.Parameters.AddWithValue("userId", group.Key.UserId);
            insert.Parameters.AddWithValue("eventKey", NotificationEventKey);
            insert.Parameters.AddWithValue("title", title);
            insert.Parameters.AddWithValue("message", message);
            insert.Parameters.AddWithValue("href", href);
            insert.Parameters.AddWithValue("entityType", group.Key.TargetType);
            insert.Parameters.AddWithValue("entityId", group.Key.TargetId);
            insert.Parameters.AddWithValue("dedupeKey", dedupeKey);
            insert.Parameters.AddWithValue("createdAt", createdAt);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not null)
            {
                notifications.Add(new(
                    notificationId,
                    group.Key.CompanyId,
                    group.Key.UserId,
                    title,
                    message,
                    createdAt,
                    dedupeKey,
                    href,
                    group.Key.TargetType,
                    group.Key.TargetId,
                    NotificationEventKey,
                    "danger"));
            }

            await using var mark = new NpgsqlCommand("update ai_agent_suggestions set priority_notified_at=now(),updated_at=now() where id=any(@ids) and priority_notified_at is null;", connection, transaction);
            mark.Parameters.AddWithValue("ids", ids);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            try { PublishNotification(notification); }
            catch (Exception exception) { logger.LogError(exception, "Failed to publish suggestion notification. NotificationId={NotificationId}", notification.Id); }
        }
    }

    public async Task PublishPendingResponseNotificationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("select pg_try_advisory_xact_lock(hashtext('suggestion-response-notifications'));", connection, transaction))
        {
            if (!Convert.ToBoolean(await lockCommand.ExecuteScalarAsync(cancellationToken)))
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
        }

        const string candidatesSql = """
            select suggestion.id, suggestion.company_id,
                   coalesce(responsible.user_id, opportunity.owner_user_id, contact.owner_user_id) as user_id,
                   contact.name,
                   suggestion.title,
                   suggestion.response_required_at,
                   case when opportunity.id is null
                       then '/crm/contacts/' || contact.id::text
                       else '/crm/opportunities/' || opportunity.id::text
                   end as href
            from ai_agent_suggestions suggestion
            inner join contacts contact on contact.id = suggestion.contact_id and contact.company_id = suggestion.company_id
            left join lateral (
                select assigned.user_id
                from contact_responsibles assigned
                inner join users assigned_user on assigned_user.id = assigned.user_id and assigned_user.is_active = true
                where assigned.contact_id = contact.id
                order by assigned.is_primary desc, assigned.created_at
                limit 1
            ) responsible on true
            left join lateral (
                select candidate.id, candidate.owner_user_id
                from opportunity_contacts relation
                inner join opportunities candidate on candidate.id = relation.opportunity_id
                where relation.contact_id = contact.id
                  and candidate.company_id = suggestion.company_id
                  and candidate.status = 'active'
                order by candidate.updated_at desc
                limit 1
            ) opportunity on true
            where suggestion.status = 'pending'
              and suggestion.suggestion_type = 'activity'
              and suggestion.response_required_at is not null
              and suggestion.response_reminder_notified_at is null
              and coalesce(responsible.user_id, opportunity.owner_user_id, contact.owner_user_id) is not null
            order by suggestion.response_required_at
            for update of suggestion skip locked
            limit 500;
            """;
        var candidates = new List<ResponseNotificationCandidate>();
        await using (var command = new NpgsqlCommand(candidatesSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetDateTime(5).ToUniversalTime(),
                    reader.GetString(6)));
            }
        }

        var notifications = new List<NotificationBatch>();
        foreach (var candidate in candidates)
        {
            var notificationId = Guid.NewGuid();
            var createdAt = DateTime.UtcNow;
            var dedupeKey = $"suggestion-response:{candidate.SuggestionId}:{candidate.ResponseRequiredAt.Ticks}";
            var title = "Cliente aguardando resposta";
            var message = Truncate($"{candidate.ContactName}: {candidate.SuggestionTitle}", 500);
            const string insertSql = """
                insert into notifications(id,company_id,user_id,event_key,title,message,severity,href,entity_type,entity_id,channels_json,dedupe_key,created_at,updated_at)
                values(@id,@companyId,@userId,@eventKey,@title,@message,'warning',@href,'suggestion',@suggestionId,
                       '[{"channel":"system","enabled":true,"status":"sent"},{"channel":"toast","enabled":true,"status":"sent"},{"channel":"browser","enabled":true,"status":"sent"},{"channel":"email","enabled":false,"status":"disabled"},{"channel":"whatsapp","enabled":false,"status":"disabled"}]'::jsonb,
                       @dedupeKey,@createdAt,@createdAt)
                on conflict do nothing
                returning id;
                """;
            await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
            insert.Parameters.AddWithValue("id", notificationId);
            insert.Parameters.AddWithValue("companyId", candidate.CompanyId);
            insert.Parameters.AddWithValue("userId", candidate.UserId);
            insert.Parameters.AddWithValue("eventKey", ResponseNotificationEventKey);
            insert.Parameters.AddWithValue("title", title);
            insert.Parameters.AddWithValue("message", message);
            insert.Parameters.AddWithValue("href", candidate.Href);
            insert.Parameters.AddWithValue("suggestionId", candidate.SuggestionId);
            insert.Parameters.AddWithValue("dedupeKey", dedupeKey);
            insert.Parameters.AddWithValue("createdAt", createdAt);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not null)
            {
                notifications.Add(new(
                    notificationId,
                    candidate.CompanyId,
                    candidate.UserId,
                    title,
                    message,
                    createdAt,
                    dedupeKey,
                    candidate.Href,
                    "suggestion",
                    candidate.SuggestionId,
                    ResponseNotificationEventKey,
                    "warning"));
            }

            await using var mark = new NpgsqlCommand(
                "update ai_agent_suggestions set response_reminder_notified_at=now(),updated_at=now() where id=@id and response_reminder_notified_at is null;",
                connection,
                transaction);
            mark.Parameters.AddWithValue("id", candidate.SuggestionId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        foreach (var notification in notifications)
        {
            try { PublishNotification(notification); }
            catch (Exception exception) { logger.LogError(exception, "Failed to publish suggestion response notification. NotificationId={NotificationId}", notification.Id); }
        }
    }

    private void PublishNotification(NotificationBatch notification)
    {
        var options = rabbitOptions.Value;
        var factory = new ConnectionFactory { Uri = new Uri(options.Uri), DispatchConsumersAsync = true };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare(options.NotificationExchange, ExchangeType.Topic, durable: true, autoDelete: false, arguments: null);
        channel.ConfirmSelect();
        var eventId = Guid.NewGuid().ToString();
        var channels = new object[]
        {
            new { channel = "system", enabled = true, deliveredAt = notification.CreatedAt, status = "sent" },
            new { channel = "toast", enabled = true, deliveredAt = notification.CreatedAt, status = "sent" },
            new { channel = "browser", enabled = true, deliveredAt = notification.CreatedAt, status = "sent" },
            new { channel = "email", enabled = false, status = "disabled" },
            new { channel = "whatsapp", enabled = false, status = "disabled" }
        };
        var payload = new
        {
            eventId,
            type = "notification.created",
            occurredAt = DateTime.UtcNow,
            companyId = notification.CompanyId,
            userId = notification.UserId,
            notificationId = notification.Id,
            eventKey = notification.EventKey,
            readAt = (DateTime?)null,
            data = new
            {
                notification = new
                {
                    id = notification.Id,
                    eventKey = notification.EventKey,
                    title = notification.Title,
                    message = notification.Message,
                    severity = notification.Severity,
                    createdAt = notification.CreatedAt,
                    readAt = (DateTime?)null,
                    href = notification.Href,
                    entityType = notification.EntityType,
                    entityId = notification.EntityId,
                    channels
                }
            }
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.MessageId = eventId;
        properties.Type = "notification.created";
        properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        channel.BasicPublish(options.NotificationExchange, $"notification.created.{notification.CompanyId}.{notification.UserId}", false, properties, body);
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
    }

    private static string Fingerprint(IEnumerable<SuggestionCompletionEvidence> evidence) =>
        Fingerprint(evidence.OrderBy(item => item.Id).Select(item => $"{item.Id}|{item.OccurredAt:O}|{item.Summary}"));

    private static string Fingerprint(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))).ToLowerInvariant();

    private static string BuildUnfulfilledNotificationMessage(IReadOnlyCollection<NotificationCandidate> candidates)
    {
        const int maxVisibleSuggestions = 3;
        const int maxPreviewLength = 160;
        var previews = candidates
            .Take(maxVisibleSuggestions)
            .Select(candidate =>
            {
                var suggestion = string.IsNullOrWhiteSpace(candidate.SuggestionDescription)
                    ? candidate.SuggestionTitle
                    : $"{candidate.SuggestionTitle} — {candidate.SuggestionDescription}";
                return $"{candidate.ContactName}: {Excerpt(suggestion.Trim(), maxPreviewLength)}";
            })
            .ToArray();
        var remainingCount = candidates.Count - previews.Length;
        var remainingSuffix = remainingCount > 0
            ? $" (+{remainingCount} {(remainingCount == 1 ? "outra sugestão" : "outras sugestões")})"
            : string.Empty;

        return Truncate($"{string.Join("; ", previews)}{remainingSuffix}", 500);
    }

    private static string Excerpt(string value, int max) =>
        value.Length <= max ? value : $"{value[..(max - 1)].TrimEnd()}…";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
