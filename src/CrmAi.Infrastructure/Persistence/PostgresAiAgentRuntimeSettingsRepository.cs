using System.Text.Json;
using CrmAi.Application;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.Persistence;

public sealed class PostgresAiAgentRuntimeSettingsRepository(NpgsqlDataSource dataSource) : IAiAgentRuntimeSettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken cancellationToken)
    {
        const string sql = """
            select agent_key, is_active, provider, model, api_key, system_prompt, debounce_minutes, context_instructions, context_entity_keys::text
            from ai_agent_settings
            where agent_key = @agentKey
              and (@companyId::uuid is null or company_id = @companyId::uuid or company_id is null)
            order by case when company_id = @companyId::uuid then 0 else 1 end, updated_at desc
            limit 1
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("agentKey", agentKey);
        command.Parameters.Add("companyId", NpgsqlDbType.Uuid).Value = string.IsNullOrWhiteSpace(companyId) ? DBNull.Value : Guid.Parse(companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return Default(agentKey);
        }

        return new AiAgentRuntimeSettings(
            reader.GetString(reader.GetOrdinal("agent_key")),
            reader.GetBoolean(reader.GetOrdinal("is_active")),
            reader.GetString(reader.GetOrdinal("provider")),
            reader.GetString(reader.GetOrdinal("model")),
            ReadNullableString(reader, "api_key"),
            reader.GetString(reader.GetOrdinal("system_prompt")),
            reader.GetInt32(reader.GetOrdinal("debounce_minutes")),
            ReadNullableString(reader, "context_instructions"),
            ParseKeys(reader.GetString(reader.GetOrdinal("context_entity_keys"))));
    }

    private static IReadOnlyCollection<string> ParseKeys(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value, SerializerOptions) ?? [];
        }
        catch
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static AiAgentRuntimeSettings Default(string agentKey) => agentKey switch
    {
        "risk-analysis" => new(agentKey, true, "openai", "gpt-4.1-mini", null, "Voce e o Risk Analysis Agent do CRM. Responda apenas com JSON valido no schema solicitado.", 1, null, ["opportunity", "account", "products", "activities", "notes", "contacts", "users", "history", "agent_insights"]),
        "daily-checkout" => new(agentKey, true, "openai", "gpt-4.1-mini", null, "Voce e o Daily Checkout Agent do CRM. Gere fechamento operacional do dia e responda apenas com JSON valido no schema solicitado.", 1, "Considere metas, atividades, oportunidades abertas, ganhas, perdidas, riscos e recomendacoes para amanha.", ["daily_metrics", "opportunities", "activities", "users", "groups", "commercial_rules"]),
        "whatsapp-conversation-analysis" => new(agentKey, true, "openai", "gpt-4.1-mini", null, "Voce e o WhatsApp Conversation Analysis Agent do CRM. Responda apenas com JSON valido no schema solicitado.", 10, null, ["opportunity", "account", "activities", "notes", "agent_insights"]),
        "meeting-service-analysis" => new(agentKey, true, "openai", "gpt-4.1-mini", null, "Voce e o Agent de Analise do Atendimento do CRM. Avalie transcricoes de reunioes do Google Meet. Identifique objecoes, oportunidades para quebra-las e proximo passo. Responda apenas com JSON valido no schema solicitado.", 1, "Analise reunioes gravadas do Google Meet. Priorize quebra de objecoes, oportunidades comerciais e proximo passo de qualificacao.", ["opportunity", "account", "activities", "notes", "contacts", "agent_insights"]),
        _ => new(agentKey, true, "openai", "gpt-4.1-mini", null, "Voce e um agent do CRM. Responda apenas no formato solicitado.", 1, null, [])
    };
}
