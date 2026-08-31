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
            select settings.agent_key,
                   settings.is_active,
                   settings.provider,
                   settings.model,
                   coalesce(
                       nullif(btrim(settings.api_key), ''),
                       (
                           select nullif(btrim(global_settings.api_key), '')
                           from ai_agent_settings global_settings
                           where global_settings.agent_key = settings.agent_key
                             and global_settings.company_id is null
                             and nullif(btrim(global_settings.api_key), '') is not null
                           order by global_settings.updated_at desc
                           limit 1
                       ),
                       case
                           when settings.agent_key = 'call-audio-analysis' then (
                               select nullif(btrim(credential_settings.api_key), '')
                               from ai_agent_settings credential_settings
                               where credential_settings.agent_key = 'meeting-service-analysis'
                                 and (
                                     credential_settings.company_id = @companyId::uuid
                                     or credential_settings.company_id is null
                                 )
                                 and lower(btrim(credential_settings.provider)) = lower(btrim(settings.provider))
                                 and nullif(btrim(credential_settings.api_key), '') is not null
                               order by
                                   case when credential_settings.company_id = @companyId::uuid then 0 else 1 end,
                                   credential_settings.updated_at desc
                               limit 1
                           )
                           when settings.agent_key = 'skopos-individual-coach' then (
                               select nullif(btrim(credential_settings.api_key), '')
                               from ai_agent_settings credential_settings
                               where credential_settings.agent_key = 'skopos-coach'
                                 and (credential_settings.company_id = @companyId::uuid or credential_settings.company_id is null)
                                 and lower(btrim(credential_settings.provider)) = lower(btrim(settings.provider))
                                 and nullif(btrim(credential_settings.api_key), '') is not null
                               order by case when credential_settings.company_id = @companyId::uuid then 0 else 1 end,
                                        credential_settings.updated_at desc
                               limit 1
                           )
                           when settings.agent_key = 'suggestion-completion-verification' then (
                               select nullif(btrim(credential_settings.api_key), '')
                               from ai_agent_settings credential_settings
                               where credential_settings.agent_key = 'whatsapp-conversation-analysis'
                                 and (credential_settings.company_id = @companyId::uuid or credential_settings.company_id is null)
                                 and lower(btrim(credential_settings.provider)) = lower(btrim(settings.provider))
                                 and nullif(btrim(credential_settings.api_key), '') is not null
                               order by case when credential_settings.company_id = @companyId::uuid then 0 else 1 end,
                                        credential_settings.updated_at desc
                               limit 1
                           )
                       end
                   ) as api_key,
                   settings.system_prompt,
                   settings.debounce_minutes,
                   settings.context_instructions,
                   settings.context_entity_keys::text
            from ai_agent_settings settings
            where settings.agent_key = @agentKey
              and (@companyId::uuid is null or settings.company_id = @companyId::uuid or settings.company_id is null)
            order by case when settings.company_id = @companyId::uuid then 0 else 1 end, settings.updated_at desc
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
        "risk-analysis" => new(agentKey, true, "openai", "gpt-5.6-terra", null, "Voce e o Risk Analysis Agent do CRM. Responda apenas com JSON valido no schema solicitado.", 1, null, ["opportunity", "account", "products", "activities", "notes", "contacts", "users", "history", "agent_insights"]),
        "daily-checkout" => new(agentKey, true, "openai", "gpt-5.6-terra", null, "Voce e o Daily Checkout Agent do CRM. Gere fechamento operacional do dia e responda apenas com JSON valido no schema solicitado.", 1, "Considere metas, atividades, contatos, oportunidades abertas, ganhas, perdidas, riscos e recomendacoes para amanha.", ["daily_metrics", "opportunities", "activities", "contacts", "users", "groups", "commercial_rules"]),
        "whatsapp-conversation-analysis" => new(agentKey, true, "openai", "gpt-5.6-luna", null, WhatsappConversationAnalysisPrompt, 10, null, ["opportunity", "account", "activities", "notes", "contacts", "users", "history", "agent_insights"]),
        "instagram-conversation-analysis" => new(agentKey, true, "openai", "gpt-5.6-luna", null, InstagramConversationAnalysisPrompt, 10, null, ["opportunity", "account", "activities", "notes", "contacts", "agent_insights"]),
        "meeting-service-analysis" => new(agentKey, true, "openai", "gpt-5.6-terra", null, "Voce e o Agent de Analise do Atendimento do CRM. Avalie transcricoes de reunioes do Google Meet. Identifique objecoes, oportunidades para quebra-las e proximo passo. Responda apenas com JSON valido no schema solicitado.", 1, "Analise reunioes gravadas do Google Meet. Priorize quebra de objecoes, oportunidades comerciais e proximo passo de qualificacao.", ["opportunity", "account", "activities", "notes", "contacts", "agent_insights"]),
        "call-audio-analysis" => new(agentKey, true, "openai", "gpt-5.6-terra", null, "Voce e o Agente de Analise de Ligacoes do CRM. Avalie transcricoes de ligacoes do WhatsApp. Gere um resumo objetivo, identifique objecoes, oportunidades para quebra-las e o proximo passo. Nao invente informacoes e responda apenas com JSON valido no schema solicitado.", 1, "Analise a ligacao mesmo sem oportunidade vinculada. Quando houver contexto comercial, use-o para enriquecer a analise sem substituir o que foi dito na gravacao.", ["opportunity", "account", "activities", "notes", "contacts", "agent_insights"]),
        "suggestion-quality-audit" => new(agentKey, true, "openai", "gpt-5.6-terra", null, "Voce e o agente de auditoria de qualidade das sugestoes de atividade do CRM. Analise somente as evidencias fornecidas, cite IDs e responda apenas com JSON valido no schema solicitado.", 1, "Recomendacoes sao consultivas e devem ser classificadas em prompt, contexto, timing, deduplicacao, logica ou UX.", ["suggestion_feedback", "agent_settings"]),
        "suggestion-completion-verification" => new(agentKey, true, "openai", "gpt-5.6-luna", null, "Voce verifica se uma acao comercial sugerida foi realizada. Compare a intencao da sugestao com evidencias registradas, aceite canais equivalentes quando cumprirem o mesmo objetivo comercial (por exemplo, ligacao no lugar de WhatsApp) e nunca invente fatos. Para criar oportunidade, exija registro real da oportunidade. Responda somente com JSON valido no schema solicitado.", 5, "Evidencias anteriores a sugestao sao apenas contexto.", ["activities", "notes", "whatsapp_messages", "instagram_messages", "opportunities", "history", "meeting_analysis"]),
        "skopos-coach" => new(agentKey, false, "openai", "gpt-5.6-terra", null, "Voce e o Skopos Coach. Consolide apenas relatorios analiticos e agregados, sem mensagens ou transcricoes brutas.", 1, "Confirme topicos com 5 evidencias, 2 colaboradores e confianca minima de 70.", ["whatsapp_analysis", "meeting_analysis", "risk_insights", "daily_checkout", "products", "commercial_attribution", "commercial_metrics"]),
        "skopos-individual-coach" => new(agentKey, false, "openai", "gpt-5.6-terra", null, "Voce e o Skopos Coach Individual. Produza um PDI objetivo com metricas calculadas e evidencias resumidas, sem rankings ou diagnosticos de personalidade.", 1, "Limite o PDI a tres prioridades mensuraveis e nunca use mensagens, transcricoes ou audio bruto.", ["whatsapp_analysis", "meeting_analysis", "risk_insights", "daily_checkout", "products", "commercial_attribution", "commercial_metrics"]),
        _ => new(agentKey, true, "openai", "gpt-5.6-terra", null, "Voce e um agent do CRM. Responda apenas no formato solicitado.", 1, null, [])
    };

    private const string WhatsappConversationAnalysisPrompt = """
        Voce e o Agente Skopos de Analise de Conversas WhatsApp do CRM.

        Objetivo:
        Analisar conversas de WhatsApp entre cliente e colaborador da empresa apos um periodo de inatividade, consolidar o historico comercial e gerar uma atualizacao incremental para registro no CRM.

        Contexto recebido:
        - previousSummary: resumo acumulado anterior da conversa, se existir.
        - newTranscript: novo trecho da conversa desde a ultima analise.
        - conversation: dados da conversa, contato, oportunidade, conta e periodo analisado.
        - opportunity/account/contacts/users: contexto comercial disponivel no CRM.
        - activities/notes/history/agent_insights: historico recente relacionado.
        - additionalContext: instrucoes adicionais configuradas pelo usuario.

        Regras principais:
        - Use previousSummary como memoria do que ja foi analisado.
        - Analise principalmente newTranscript.
        - Atualize o resumo de forma incremental.
        - Nao repita desnecessariamente informacoes que ja estao no resumo anterior.
        - Nao invente fatos, intencoes, valores, prazos ou compromissos.
        - Diferencie mensagens do cliente e da equipe.
        - Identifique compromissos assumidos, objecoes, duvidas, proposta, pagamento, urgencia e proximos passos.
        - Considere que o sistema sempre registrara/atualizara uma atividade diaria do tipo Agente Skopos; sua funcao e gerar conteudo util para esse registro.
        - Se houver uma acao clara, preencha a sugestao de atividade.
        - Se houver apenas informacao relevante, preencha a sugestao de nota.
        - Se nao houver acao clara nem nota relevante, mantenha os campos correspondentes vazios ou false, mas ainda gere um resumo objetivo da interacao.

        Criterios para sugerir atividade:
        Sugira atividade quando houver:
        - retorno prometido;
        - necessidade de follow-up;
        - pedido de proposta, documento ou orcamento;
        - reuniao, ligacao ou demonstracao a agendar;
        - pendencia de pagamento;
        - objecao que exige resposta;
        - proximo passo comercial claro;
        - risco de perda ou urgencia.

        Criterios para sugerir nota:
        Sugira nota quando houver:
        - informacao comercial relevante;
        - preferencia do cliente;
        - objecao, duvida ou condicao;
        - detalhe de proposta, produto, prazo ou pagamento;
        - mudanca de contexto;
        - informacao que deve ficar registrada, mas nao exige tarefa.

        Formato de resposta:
        Responda estritamente no JSON schema configurado pelo sistema:
        - conversationSummary: resumo atualizado e incremental da conversa.
        - shouldCreateNote: true/false.
        - noteText: texto objetivo da nota, se aplicavel.
        - shouldCreateActivity: true/false.
        - activityTitle: titulo curto da acao sugerida, se aplicavel.
        - activityNotes: detalhes da acao sugerida, se aplicavel.
        - activityDueAt: data/hora ISO 8601 se houver prazo claro; caso contrario null.
        - confidenceScore: numero de 0 a 100.
        - reasons: lista curta com motivos da analise.

        Tom:
        - Profissional.
        - Direto.
        - Comercial.
        - Objetivo.
        - Sem exageros.
        - Sem inventar informacoes ausentes.
        """;

    private const string InstagramConversationAnalysisPrompt = """
        Voce e o Instagram Conversation Analysis Agent do CRM.
        Consolide o resumo anterior com a nova mensagem do Instagram sem perder fatos comerciais.
        Identifique intencao de compra, objecoes, urgencia e proximos passos.
        Crie nota somente quando houver informacao comercial relevante.
        Crie atividade somente quando existir uma acao clara, prazo, reuniao, proposta, pagamento ou follow-up.
        Responda exclusivamente no JSON solicitado e escreva em portugues.
        """;
}
