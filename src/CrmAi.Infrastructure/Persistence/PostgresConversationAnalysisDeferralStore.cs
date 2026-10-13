using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresConversationAnalysisDeferralStore(NpgsqlDataSource dataSource) : IConversationAnalysisDeferralStore
{
    public async Task MarkDeferredAsync(OpportunityEvent opportunityEvent, string reason, CancellationToken cancellationToken)
    {
        if (!GetGuid(opportunityEvent, "companyId", out var companyId)
            || !GetGuid(opportunityEvent, "conversationId", out var conversationId)) return;

        string sql;
        Guid? runId = null;
        if (string.Equals(opportunityEvent.Type, "opportunity.whatsapp.conversation.batch", StringComparison.OrdinalIgnoreCase))
        {
            if (!GetGuid(opportunityEvent, "runId", out var parsedRunId)
                || !Guid.TryParse(opportunityEvent.EventId, out var eventId) || eventId != parsedRunId) return;
            runId = parsedRunId;
            sql = """
                with deferred_run as (
                    update whatsapp_conversation_analysis_runs run
                    set status='deferred', error=@reason, updated_at=now()
                    from whatsapp_conversations conversation
                    where run.id=@run and run.conversation_id=@conversation and run.company_id=@company
                      and conversation.id=run.conversation_id and conversation.company_id=run.company_id
                      and run.status in ('processing','queued','failed','deferred')
                    returning run.conversation_id,run.company_id
                )
                update whatsapp_conversations conversation
                set last_analysis_status='deferred',last_analysis_at=now(),updated_at=now()
                from deferred_run run
                where conversation.id=run.conversation_id and conversation.company_id=run.company_id
                  and conversation.last_analysis_status in ('processing','queued','failed','deferred');
                """;
        }
        else if (string.Equals(opportunityEvent.Type, "opportunity.instagram.conversation.batch", StringComparison.OrdinalIgnoreCase))
        {
            sql = """
                update instagram_conversations
                set last_analysis_status='deferred',last_analysis_at=now(),updated_at=now()
                where id=@conversation and company_id=@company and channel='instagram'
                  and last_analysis_status in ('processing','queued','failed','deferred');
                """;
        }
        else return;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("conversation", conversationId);
        if (runId is not null)
        {
            command.Parameters.AddWithValue("run", runId.Value);
            // Persist operational codes, never a provider response or credential.
            command.Parameters.AddWithValue("reason", SafeReason(reason));
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string SafeReason(string reason) => reason is "credential_missing" or "credential_rejected"
        or "provider_quota_exhausted" or "internal_credits_exhausted" ? reason : "analysis_temporarily_unavailable";

    private static bool GetGuid(OpportunityEvent opportunityEvent, string key, out Guid value)
    {
        value = Guid.Empty;
        return opportunityEvent.Data.TryGetValue(key, out var raw) && Guid.TryParse(raw?.ToString(), out value) && value != Guid.Empty;
    }
}
