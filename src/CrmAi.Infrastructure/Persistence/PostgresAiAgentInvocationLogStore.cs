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
        await using var command = new NpgsqlCommand(sql, connection);
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
