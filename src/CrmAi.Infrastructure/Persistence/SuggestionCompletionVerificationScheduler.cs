using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

/// <summary>
/// Releases existing activity suggestions for a new semantic verification when
/// an analysis has added evidence for the same contact or opportunity.
/// </summary>
internal static class SuggestionCompletionVerificationScheduler
{
    public static async Task RequestForScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid? companyId,
        Guid? contactId,
        Guid? opportunityId,
        CancellationToken cancellationToken)
    {
        if (companyId is null || (contactId is null && opportunityId is null))
        {
            return;
        }

        const string sql = """
            with timing as (
                select now() + interval '1 minute' * greatest(1, least(1440, coalesce((
                    select settings.debounce_minutes
                    from ai_agent_settings settings
                    where settings.agent_key = 'suggestion-completion-verification'
                      and (settings.company_id = @companyId or settings.company_id is null)
                    order by case when settings.company_id = @companyId then 0 else 1 end,
                             settings.updated_at desc
                    limit 1
                ), 5))) as requested_at
            )
            update ai_agent_suggestions suggestion
            set next_verification_at = case
                    -- New evidence cannot cancel a failed invocation's backoff.
                    when suggestion.verification_status = 'failed' and suggestion.next_verification_at > now()
                        then suggestion.next_verification_at
                    -- Keep the worker's lease timestamp; a changed next date records
                    -- evidence received during its call for the completion path.
                    when suggestion.verification_status = 'processing'
                         and suggestion.next_verification_at = suggestion.updated_at + interval '15 minutes'
                        then timing.requested_at
                    when suggestion.next_verification_at is not null
                        then least(suggestion.next_verification_at, timing.requested_at)
                    else timing.requested_at
                end,
                updated_at = case when suggestion.verification_status = 'processing'
                                  then suggestion.updated_at else now() end
            from timing
            where suggestion.company_id = @companyId
              and suggestion.status = 'pending'
              and suggestion.suggestion_type = 'activity'
              and (
                    suggestion.contact_id = @contactId
                    or (
                        @opportunityId is not null
                        and (
                            suggestion.contact_id in (
                                select relation.contact_id
                                from opportunity_contacts relation
                                where relation.opportunity_id = @opportunityId
                            )
                            or suggestion.contact_id in (
                                select contact.id
                                from contacts contact
                                inner join opportunities opportunity
                                    on opportunity.account_id = contact.account_id
                                where opportunity.id = @opportunityId
                                  and opportunity.company_id = @companyId
                                  and contact.company_id = @companyId
                            )
                        )
                    )
                  );
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("companyId", companyId.Value);
        command.Parameters.Add("contactId", NpgsqlDbType.Uuid).Value = contactId is null ? DBNull.Value : contactId.Value;
        command.Parameters.Add("opportunityId", NpgsqlDbType.Uuid).Value = opportunityId is null ? DBNull.Value : opportunityId.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
