using CrmAi.Application;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresWhatsappSuggestionContextRepository(NpgsqlDataSource dataSource) : IWhatsappSuggestionContextRepository
{
    public async Task<WhatsappSuggestionSemanticContext> GetAsync(
        string? companyId,
        string? contactId,
        CancellationToken cancellationToken) =>
        await GetAsync(companyId, contactId, "whatsapp-conversation-analysis", cancellationToken);

    public async Task<WhatsappSuggestionSemanticContext> GetAsync(
        string? companyId,
        string? contactId,
        string agentKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(companyId, out var parsedCompanyId) || !Guid.TryParse(contactId, out var parsedContactId))
        {
            return WhatsappSuggestionSemanticContext.Empty;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var suggestions = await ReadSuggestionsAsync(connection, parsedCompanyId, parsedContactId, agentKey, cancellationToken);
        var opportunities = await ReadOpenOpportunitiesAsync(connection, parsedCompanyId, parsedContactId, cancellationToken);
        return new WhatsappSuggestionSemanticContext(suggestions, opportunities);
    }

    private static async Task<IReadOnlyCollection<WhatsappSuggestionCandidate>> ReadSuggestionsAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid contactId,
        string agentKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, suggestion_type, status, title, description, suggested_due_at,
                   payload ->> 'semanticIntentKey' as semantic_intent_key, updated_at
            from ai_agent_suggestions
            where company_id = @companyId
              and agent_key = @agentKey
              and contact_id = @contactId
              and (
                status in ('pending', 'rejected')
                or (
                  status = 'accepted'
                  and resolved_at >= now() - interval '30 days'
                )
              )
            order by updated_at desc
            limit 30;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("contactId", contactId);
        command.Parameters.AddWithValue("agentKey", agentKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<WhatsappSuggestionCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WhatsappSuggestionCandidate(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToUniversalTime(),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetDateTime(7).ToUniversalTime()));
        }

        return rows;
    }

    private static async Task<IReadOnlyCollection<WhatsappOpenOpportunityCandidate>> ReadOpenOpportunitiesAsync(
        NpgsqlConnection connection,
        Guid companyId,
        Guid contactId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select distinct opportunity.id, opportunity.name, pipeline.name, stage.title, opportunity.updated_at
            from opportunities opportunity
            inner join pipelines pipeline on pipeline.id = opportunity.pipeline_id
            inner join pipeline_stages stage on stage.id = opportunity.stage_id
            left join opportunity_contacts link on link.opportunity_id = opportunity.id
            left join contacts contact on contact.id = @contactId
            where opportunity.company_id = @companyId
              and opportunity.status = 'active'
              and (link.contact_id = @contactId or opportunity.account_id = contact.account_id)
            order by opportunity.updated_at desc
            limit 30;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = companyId;
        command.Parameters.Add("contactId", NpgsqlDbType.Uuid).Value = contactId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<WhatsappOpenOpportunityCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WhatsappOpenOpportunityCandidate(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDateTime(4).ToUniversalTime()));
        }

        return rows;
    }
}
