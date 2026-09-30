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
            update ai_agent_suggestions suggestion
            set next_verification_at = now(),
                updated_at = now()
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
