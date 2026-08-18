using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresWhatsappScorecardContextRepository(NpgsqlDataSource dataSource) : IWhatsappScorecardContextRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WhatsappScorecardContext?> GetAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        if (!TryGetGuid(opportunityEvent, "companyId", out var companyId)
            || !TryGetGuid(opportunityEvent, "conversationId", out var conversationId))
        {
            return null;
        }

        var opportunityId = Guid.TryParse(opportunityEvent.OpportunityId, out var parsedOpportunityId)
            && parsedOpportunityId != Guid.Empty ? parsedOpportunityId : (Guid?)null;
        var ownerUserId = GetGuid(opportunityEvent, "ownerUserId")
            ?? (Guid.TryParse(opportunityEvent.UserId, out var parsedUserId) ? parsedUserId : null);
        var occurredAt = GetDateTime(opportunityEvent, "latestMessageAt") ?? opportunityEvent.OccurredAt;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string templateSql = """
            with scope as (
                select opportunity.pipeline_id, opportunity.stage_id,
                       owner.group_id
                from (select 1) seed
                left join opportunities opportunity
                  on opportunity.id = @opportunityId and opportunity.company_id = @companyId
                left join users owner
                  on owner.id = coalesce(@ownerUserId, opportunity.owner_user_id)
                 and owner.company_id = @companyId
            )
            select template.id, template.template_key, template.version, template.name
            from conversation_scorecard_templates template
            cross join scope
            where template.company_id = @companyId
              and template.status = 'published'
              and (template.valid_from is null or template.valid_from <= @occurredAt)
              and (template.valid_to is null or template.valid_to > @occurredAt)
              and (template.source_kind is null or template.source_kind = 'whatsapp_conversation')
              and (template.pipeline_id is null or template.pipeline_id = scope.pipeline_id)
              and (template.stage_id is null or template.stage_id = scope.stage_id)
              and (template.group_id is null or template.group_id = scope.group_id)
              and (template.activity_type is null or template.activity_type = 'agent-skopos')
            order by template.priority desc,
              ((template.pipeline_id is not null)::int + (template.stage_id is not null)::int
               + (template.group_id is not null)::int + (template.activity_type is not null)::int
               + (template.source_kind is not null)::int) desc,
              template.version desc, template.published_at desc
            limit 1
            """;

        Guid templateId;
        Guid templateKey;
        int templateVersion;
        string templateName;
        await using (var command = new NpgsqlCommand(templateSql, connection))
        {
            command.Parameters.AddWithValue("companyId", companyId);
            command.Parameters.Add("opportunityId", NpgsqlDbType.Uuid).Value = opportunityId ?? (object)DBNull.Value;
            command.Parameters.Add("ownerUserId", NpgsqlDbType.Uuid).Value = ownerUserId ?? (object)DBNull.Value;
            command.Parameters.AddWithValue("occurredAt", occurredAt.ToUniversalTime());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            templateId = reader.GetGuid(0);
            templateKey = reader.GetGuid(1);
            templateVersion = reader.GetInt32(2);
            templateName = reader.GetString(3);
        }

        var criteria = new List<WhatsappScorecardCriterionContext>();
        const string criteriaSql = """
            select id, criterion_key, title, description, weight, evaluation_instruction,
                   positive_examples::text, negative_examples::text,
                   score_min, score_max, is_required
            from conversation_scorecard_criteria
            where template_id = @templateId
            order by position, title
            """;
        await using (var command = new NpgsqlCommand(criteriaSql, connection))
        {
            command.Parameters.AddWithValue("templateId", templateId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                criteria.Add(new WhatsappScorecardCriterionContext(
                    reader.GetGuid(0).ToString(),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetDecimal(4),
                    reader.GetString(5),
                    ParseStringArray(reader.GetString(6)),
                    ParseStringArray(reader.GetString(7)),
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.GetBoolean(10)));
            }
        }
        if (criteria.Count == 0) return null;

        var previousItems = new List<WhatsappPreviousScorecardItemInput>();
        const string previousSql = """
            with settings as (
                select coalesce(
                    (select time_zone_id from daily_checkout_settings where company_id = @companyId limit 1),
                    'America/Sao_Paulo') time_zone_id
            ), current_scorecard as (
                select scorecard.id
                from conversation_scorecards scorecard
                join whatsapp_conversation_analysis_runs run
                  on run.id = scorecard.whatsapp_analysis_run_id
                cross join settings
                where scorecard.company_id = @companyId
                  and scorecard.source_kind = 'whatsapp_conversation'
                  and scorecard.template_key = @templateKey
                  and scorecard.is_current
                  and run.conversation_id = @conversationId
                  and (run.window_end_at at time zone settings.time_zone_id)::date =
                      (@occurredAt at time zone settings.time_zone_id)::date
                order by scorecard.created_at desc
                limit 1
            )
            select item.criterion_key,
                   case when scorecard.status = 'reviewed'
                        then coalesce(item.reviewed_score, item.ai_score)
                        else item.ai_score end effective_score,
                   item.confidence_score,
                   item.justification, item.recommendation, item.evidence_json::text
            from conversation_scorecard_items item
            join conversation_scorecards scorecard on scorecard.id = item.scorecard_id
            where item.scorecard_id = (select id from current_scorecard)
            order by item.created_at, item.criterion_title
            """;
        await using (var command = new NpgsqlCommand(previousSql, connection))
        {
            command.Parameters.AddWithValue("companyId", companyId);
            command.Parameters.AddWithValue("conversationId", conversationId);
            command.Parameters.AddWithValue("templateKey", templateKey);
            command.Parameters.AddWithValue("occurredAt", occurredAt.ToUniversalTime());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                previousItems.Add(new WhatsappPreviousScorecardItemInput(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    Truncate(reader.GetString(3), 500),
                    reader.IsDBNull(4) ? null : Truncate(reader.GetString(4), 300),
                    ParseEvidence(reader.GetString(5))
                        .Where(item => !string.IsNullOrWhiteSpace(item.Excerpt))
                        .Select(item => item with { Excerpt = Truncate(item.Excerpt, 300) })
                        .Take(2)
                        .ToArray()));
            }
        }

        return new WhatsappScorecardContext(
            templateId.ToString(),
            templateKey.ToString(),
            templateVersion,
            templateName,
            criteria,
            previousItems);
    }

    private static string[] ParseStringArray(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static OpenAiConversationEvidence[] ParseEvidence(string value)
    {
        try { return JsonSerializer.Deserialize<OpenAiConversationEvidence[]>(value, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static Guid? GetGuid(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) && Guid.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : null;

    private static bool TryGetGuid(OpportunityEvent opportunityEvent, string key, out Guid value)
    {
        value = GetGuid(opportunityEvent, key) ?? Guid.Empty;
        return value != Guid.Empty;
    }

    private static DateTime? GetDateTime(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) && DateTime.TryParse(value?.ToString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
