using System.Text.Json;
using System.Runtime.CompilerServices;
using CrmAi.Application;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Configuration;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresAiAgentInvocationLogStore(NpgsqlDataSource dataSource, IConfiguration configuration) : IAiAgentInvocationLogStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ConditionalWeakTable<AiAgentInvocationContext, ReservationHandle> reservations = new();
    private readonly long defaultReservationCredits = Math.Clamp(configuration.GetValue<long?>("AiCredits:DefaultReservationCredits") ?? 100, 1, 100_000);
    private readonly bool meteringEnabled = configuration.GetValue("Saas:AiCreditMeteringEnabled", true);
    private readonly bool enforcementEnabled = configuration.GetValue("Saas:AiCreditMeteringEnabled", true) && configuration.GetValue("Saas:AiCreditEnforcementEnabled", true);

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
        if (Guid.TryParse(entry.Context.CompanyId, out var companyId))
        {
            reservations.TryGetValue(entry.Context, out var reservation);
            if (entry.Success)
                await ChargeAsync(connection, transaction, companyId, entry, reservation?.InvocationId, meteringEnabled, enforcementEnabled, cancellationToken);
            else if (reservation is not null)
                await SetReservationStatusAsync(connection, transaction, companyId, reservation.InvocationId, "released", cancellationToken);
            reservations.Remove(entry.Context);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureCreditsAvailableAsync(AiAgentInvocationContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.CompanyId, out var companyId)) return;
        if (!enforcementEnabled) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCompanyAsync(connection, transaction, companyId, cancellationToken);
        if (await IsUnlimitedAsync(connection, transaction, companyId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await using (var expire = new NpgsqlCommand("update ai_credit_reservations set status = 'expired', updated_at = now() where company_id = @companyId and status = 'reserved' and expires_at <= now();", connection, transaction))
        {
            expire.Parameters.AddWithValue("companyId", companyId);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        long available;
        await using (var balance = new NpgsqlCommand("select coalesce(sum(remaining_credits), 0) - coalesce((select sum(reserved_credits) from ai_credit_reservations where company_id = @companyId and status = 'reserved' and expires_at > now()), 0) from ai_credit_lots where company_id = @companyId and remaining_credits > 0 and (expires_at is null or expires_at > now());", connection, transaction))
        {
            balance.Parameters.AddWithValue("companyId", companyId);
            available = Convert.ToInt64(await balance.ExecuteScalarAsync(cancellationToken));
        }
        if (available <= 0) throw new AiCreditsExhaustedException();
        var invocationId = Guid.NewGuid().ToString("N");
        var reservedCredits = Math.Min(defaultReservationCredits, available);
        await using (var reserve = new NpgsqlCommand("insert into ai_credit_reservations(company_id, invocation_id, reserved_credits, status, expires_at) values(@companyId, @invocationId, @reservedCredits, 'reserved', now() + interval '30 minutes');", connection, transaction))
        {
            reserve.Parameters.AddWithValue("companyId", companyId);
            reserve.Parameters.AddWithValue("invocationId", invocationId);
            reserve.Parameters.AddWithValue("reservedCredits", reservedCredits);
            await reserve.ExecuteNonQueryAsync(cancellationToken);
        }
        reservations.Remove(context);
        reservations.Add(context, new ReservationHandle(invocationId));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ChargeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, AiAgentInvocationLogEntry entry, string? reservationInvocationId, bool meteringEnabled, bool enforcementEnabled, CancellationToken cancellationToken)
    {
        var input = Math.Max(0, (entry.Usage.PromptTokens ?? 0) - (entry.Usage.CachedPromptTokens ?? 0));
        var output = Math.Max(0, entry.Usage.CompletionTokens ?? 0);
        var cached = Math.Max(0, entry.Usage.CachedPromptTokens ?? 0);
        await LockCompanyAsync(connection, transaction, companyId, cancellationToken);
        if (await IsUnlimitedAsync(connection, transaction, companyId, cancellationToken))
        {
            if (meteringEnabled) await InsertUsageEventAsync(connection, transaction, companyId, entry, 0, "unlimited", cancellationToken);
            return;
        }
        if (!meteringEnabled) return;

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
        await InsertUsageEventAsync(connection, transaction, companyId, entry, requestedCharge, enforcementEnabled ? "enforced" : "shadow", cancellationToken);
        if (!enforcementEnabled) return;
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
        var otherReservations = 0L;
        await using (var reservedCommand = new NpgsqlCommand("select coalesce(sum(reserved_credits), 0) from ai_credit_reservations where company_id = @companyId and status = 'reserved' and expires_at > now() and (@invocationId is null or invocation_id <> @invocationId);", connection, transaction))
        {
            reservedCommand.Parameters.AddWithValue("companyId", companyId);
            reservedCommand.Parameters.AddWithValue("invocationId", (object?)reservationInvocationId ?? DBNull.Value);
            otherReservations = Convert.ToInt64(await reservedCommand.ExecuteScalarAsync(cancellationToken));
        }
        var available = Math.Max(0, lots.Sum(lot => lot.Balance) - otherReservations);
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
        if (reservationInvocationId is not null)
            await SetReservationStatusAsync(connection, transaction, companyId, reservationInvocationId, "settled", cancellationToken);
    }

    private static async Task LockCompanyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended(@companyId::text, 0));", connection, transaction);
        command.Parameters.AddWithValue("companyId", companyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsUnlimitedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from saas_contracts where company_id = @companyId and status = 'legacy_unlimited');", connection, transaction);
        command.Parameters.AddWithValue("companyId", companyId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task SetReservationStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, string invocationId, string status, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("update ai_credit_reservations set status = @status, updated_at = now() where company_id = @companyId and invocation_id = @invocationId and status = 'reserved';", connection, transaction);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("invocationId", invocationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertUsageEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid companyId, AiAgentInvocationLogEntry entry, long credits, string mode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into ai_usage_events(company_id, invocation_id, provider, model, operation, input_tokens, output_tokens, cached_input_tokens, calculated_credits, metering_mode)
            values(@companyId, @invocationId, @provider, @model, @operation, @input, @output, @cached, @credits, @mode)
            on conflict(company_id, invocation_id) do nothing;
            """, connection, transaction);
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("invocationId", entry.Id.ToString("N"));
        command.Parameters.AddWithValue("provider", entry.Provider);
        command.Parameters.AddWithValue("model", entry.Model);
        command.Parameters.AddWithValue("operation", entry.Operation);
        command.Parameters.AddWithValue("input", Math.Max(0, entry.Usage.PromptTokens ?? 0));
        command.Parameters.AddWithValue("output", Math.Max(0, entry.Usage.CompletionTokens ?? 0));
        command.Parameters.AddWithValue("cached", Math.Max(0, entry.Usage.CachedPromptTokens ?? 0));
        command.Parameters.AddWithValue("credits", credits);
        command.Parameters.AddWithValue("mode", mode);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed record ReservationHandle(string InvocationId);
}
