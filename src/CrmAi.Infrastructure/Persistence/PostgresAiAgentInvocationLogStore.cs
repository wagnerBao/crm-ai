using System.Text.Json;
using CrmAi.Application;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresAiAgentInvocationLogStore(NpgsqlDataSource dataSource) : IAiAgentInvocationLogStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into ai_agent_invocation_logs (
                id,
                agent_key,
                provider,
                model,
                operation,
                platform_area,
                endpoint,
                http_status,
                success,
                status,
                request_json,
                response_json,
                result_json,
                error_type,
                error_message,
                prompt_tokens,
                completion_tokens,
                total_tokens,
                cached_prompt_tokens,
                reasoning_tokens,
                company_id,
                opportunity_id,
                whatsapp_conversation_id,
                meeting_audio_recording_id,
                activity_id,
                account_id,
                contact_id,
                user_id,
                context_entity_keys,
                metadata_json,
                started_at,
                completed_at,
                duration_ms,
                created_at)
            values (
                @id,
                @agentKey,
                @provider,
                @model,
                @operation,
                @platformArea,
                @endpoint,
                @httpStatus,
                @success,
                @status,
                @requestJson::jsonb,
                @responseJson::jsonb,
                @resultJson::jsonb,
                @errorType,
                @errorMessage,
                @promptTokens,
                @completionTokens,
                @totalTokens,
                @cachedPromptTokens,
                @reasoningTokens,
                @companyId,
                @opportunityId,
                @whatsappConversationId,
                @meetingAudioRecordingId,
                @activityId,
                @accountId,
                @contactId,
                @userId,
                @contextEntityKeys,
                @metadataJson::jsonb,
                @startedAt,
                @completedAt,
                @durationMs,
                now())
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", entry.Id);
        command.Parameters.AddWithValue("agentKey", entry.AgentKey);
        command.Parameters.AddWithValue("provider", entry.Provider);
        command.Parameters.AddWithValue("model", entry.Model);
        command.Parameters.AddWithValue("operation", entry.Operation);
        command.Parameters.AddWithValue("platformArea", entry.PlatformArea);
        command.Parameters.AddWithValue("endpoint", entry.Endpoint);
        AddNullable(command, "httpStatus", NpgsqlDbType.Integer, entry.HttpStatus);
        command.Parameters.AddWithValue("success", entry.Success);
        command.Parameters.AddWithValue("status", entry.Status);
        command.Parameters.AddWithValue("requestJson", entry.RequestJson);
        AddNullable(command, "responseJson", NpgsqlDbType.Jsonb, entry.ResponseJson);
        AddNullable(command, "resultJson", NpgsqlDbType.Jsonb, entry.ResultJson);
        AddNullable(command, "errorType", NpgsqlDbType.Text, entry.ErrorType);
        AddNullable(command, "errorMessage", NpgsqlDbType.Text, entry.ErrorMessage);
        AddNullable(command, "promptTokens", NpgsqlDbType.Integer, entry.Usage.PromptTokens);
        AddNullable(command, "completionTokens", NpgsqlDbType.Integer, entry.Usage.CompletionTokens);
        AddNullable(command, "totalTokens", NpgsqlDbType.Integer, entry.Usage.TotalTokens);
        AddNullable(command, "cachedPromptTokens", NpgsqlDbType.Integer, entry.Usage.CachedPromptTokens);
        AddNullable(command, "reasoningTokens", NpgsqlDbType.Integer, entry.Usage.ReasoningTokens);
        AddGuid(command, "companyId", entry.Context.CompanyId);
        AddGuid(command, "opportunityId", entry.Context.OpportunityId);
        AddGuid(command, "whatsappConversationId", entry.Context.WhatsappConversationId);
        AddGuid(command, "meetingAudioRecordingId", entry.Context.MeetingAudioRecordingId);
        AddGuid(command, "activityId", entry.Context.ActivityId);
        AddGuid(command, "accountId", entry.Context.AccountId);
        AddGuid(command, "contactId", entry.Context.ContactId);
        AddGuid(command, "userId", entry.Context.UserId);
        command.Parameters.AddWithValue("contextEntityKeys", (entry.Context.ContextEntityKeys ?? []).ToArray());
        AddNullable(command, "metadataJson", NpgsqlDbType.Jsonb, SerializeMetadata(entry.Context.Metadata));
        command.Parameters.AddWithValue("startedAt", NpgsqlDbType.TimestampTz, entry.StartedAt);
        command.Parameters.AddWithValue("completedAt", NpgsqlDbType.TimestampTz, entry.CompletedAt);
        command.Parameters.AddWithValue("durationMs", entry.DurationMs);

        await command.ExecuteNonQueryAsync(cancellationToken);
        if (entry.Success && Guid.TryParse(entry.Context.CompanyId, out var companyId))
        {
            await ChargeAsync(connection, transaction, companyId, entry, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureCreditsAvailableAsync(AiAgentInvocationContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.CompanyId, out var companyId)) return;
        const string sql = """
            select exists(select 1 from saas_contracts where company_id = @companyId and status = 'legacy_unlimited')
                or exists(select 1 from ai_credit_lots where company_id = @companyId and remaining_credits > 0 and (expires_at is null or expires_at > now()));
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", companyId);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken))) throw new AiCreditsExhaustedException();
    }

    private static async Task ChargeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, AiAgentInvocationLogEntry entry, CancellationToken cancellationToken)
    {
        var input = Math.Max(0, (entry.Usage.PromptTokens ?? 0) - (entry.Usage.CachedPromptTokens ?? 0));
        var output = Math.Max(0, entry.Usage.CompletionTokens ?? 0);
        var cached = Math.Max(0, entry.Usage.CachedPromptTokens ?? 0);
        if (input + output + cached == 0) return;

        await using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@companyId::text, 0));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("companyId", companyId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var unlimitedCommand = new NpgsqlCommand("select exists(select 1 from saas_contracts where company_id = @companyId and status = 'legacy_unlimited');", connection, transaction))
        {
            unlimitedCommand.Parameters.AddWithValue("companyId", companyId);
            if (Convert.ToBoolean(await unlimitedCommand.ExecuteScalarAsync(cancellationToken))) return;
        }

        decimal inputRate;
        decimal outputRate;
        decimal cachedRate;
        await using (var rateCommand = new NpgsqlCommand("""
            select input_credits_per_thousand, output_credits_per_thousand, cached_credits_per_thousand
              from ai_rate_cards
             where lower(provider) = lower(@provider) and (model_pattern = '*' or lower(model_pattern) = lower(@model))
               and effective_from <= now() and (effective_to is null or effective_to > now())
             order by case when model_pattern = '*' then 1 else 0 end, effective_from desc limit 1;
            """, connection, transaction))
        {
            rateCommand.Parameters.AddWithValue("provider", entry.Provider);
            rateCommand.Parameters.AddWithValue("model", entry.Model);
            await using var reader = await rateCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return;
            inputRate = reader.GetDecimal(0); outputRate = reader.GetDecimal(1); cachedRate = reader.GetDecimal(2);
        }
        var requestedCharge = Math.Max(1L, (long)Math.Ceiling((input * inputRate + output * outputRate + cached * cachedRate) / 1000m));
        var remaining = requestedCharge;
        var lots = new List<(Guid Id, long Balance)>();
        await using (var lotsCommand = new NpgsqlCommand("""
            select id, remaining_credits from ai_credit_lots
             where company_id = @companyId and remaining_credits > 0 and (expires_at is null or expires_at > now())
             order by case when source_type = 'allowance' then 0 else 1 end, expires_at nulls last, created_at for update;
            """, connection, transaction))
        {
            lotsCommand.Parameters.AddWithValue("companyId", companyId);
            await using var reader = await lotsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lots.Add((reader.GetGuid(0), reader.GetInt64(1)));
        }
        var available = lots.Sum(lot => lot.Balance);
        var charge = Math.Min(requestedCharge, available);
        remaining = charge;
        foreach (var lot in lots)
        {
            if (remaining <= 0) break;
            var debit = Math.Min(remaining, lot.Balance);
            await using var update = new NpgsqlCommand("update ai_credit_lots set remaining_credits = remaining_credits - @debit where id = @id;", connection, transaction);
            update.Parameters.AddWithValue("debit", debit); update.Parameters.AddWithValue("id", lot.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
            remaining -= debit;
        }
        if (charge <= 0) return;
        await using var ledger = new NpgsqlCommand("""
            insert into ai_credit_ledger_entries (company_id, entry_type, credits, description, invocation_id)
            values (@companyId, 'consume', @credits, @description, @invocationId)
            on conflict (company_id, invocation_id, entry_type) where invocation_id is not null do nothing;
            """, connection, transaction);
        ledger.Parameters.AddWithValue("companyId", companyId);
        ledger.Parameters.AddWithValue("credits", -charge);
        ledger.Parameters.AddWithValue("description", $"{entry.Operation} · {entry.Provider}/{entry.Model}");
        ledger.Parameters.AddWithValue("invocationId", entry.Id.ToString("N"));
        await ledger.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata) =>
        metadata is null || metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata, SerializerOptions);

    private static void AddGuid(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value =
            Guid.TryParse(value, out var parsed) ? parsed : DBNull.Value;
    }

    private static void AddNullable<T>(NpgsqlCommand command, string name, NpgsqlDbType type, T? value)
    {
        command.Parameters.Add(name, type).Value = value is null ? DBNull.Value : value;
    }
}
