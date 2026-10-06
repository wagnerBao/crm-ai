using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiResponsesWhatsappConversationAnalysisClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiWhatsappConversationAnalysisClient
{
    private const string ParticipantIdentityInstructions = """
        Identidade dos participantes e perspectiva obrigatoria:
        - participants.contactId/contactName identificam o contato externo; participants.ownerUserId/ownerUserName identificam o responsavel interno do CRM. participants.mailboxName identifica a caixa da equipe, nao o contato.
        - Mensagens incoming/Cliente foram recebidas do contato externo; mensagens outgoing/Equipe foram enviadas pela equipe ao contato. A direcao prevalece sobre nomes de remetente, vocativos, assinaturas e nomes citados no texto.
        - O responsavel do CRM nao e necessariamente o autor de toda mensagem outgoing. Nao invente a identidade do autor quando ela nao estiver confirmada.
        - Antes de sugerir uma acao, identifique quem fez o pedido, quem deve executa-lo, quem recebera a resposta e se mensagens posteriores ja resolveram a pendencia.
        - Toda atividade e nextStep deve ser executavel pela equipe interna, do ponto de vista do responsavel do CRM. Uma resposta da equipe deve ser dirigida ao contato externo, nunca ao proprio responsavel.
        - Um pedido outgoing da equipe ao contato nao e um pedido recebido do cliente. Nao transforme uma tarefa do contato em obrigacao da equipe de executar ou responder ao proprio pedido; sugira acompanhamento somente se houver pendencia sustentada pela conversa.
        - Registre compromissos do contato como compromissos do contato, sem atribui-los automaticamente a equipe.
        - Respostas posteriores, confirmacoes de solucao, agradecimentos e encerramentos devem ser considerados antes de sugerir novamente a mesma acao.
        - previousSummary e existingSuggestions podem conter atribuicoes antigas incorretas; corrija-as quando conflitarem com participants e a direcao de newTranscript. Nao reproduza uma sugestao que troca os papeis apenas para manter a deduplicacao.
        """;
    private const string SemanticDeduplicationInstructions = """
        Deduplicacao semantica obrigatoria:
        - Compare cada atividade ou oportunidade sugerida com existingSuggestions e existingOpenOpportunities.
        - A decisao deve considerar o intuito comercial, a acao esperada, o objeto/produto/veiculo, o problema tratado e o resultado pretendido; nao use apenas igualdade textual.
        - Mudanca de redacao, detalhe, prazo ou data de uma mesma pendencia continua sendo o mesmo intuito e deve atualizar a sugestao existente.
        - Produtos, veiculos, necessidades, problemas ou resultados comerciais materialmente diferentes representam intuitos distintos e podem gerar novos registros.
        - Quando houver o mesmo intuito, retorne exatamente o id do candidato em activityMatchingSuggestionId, opportunityMatchingSuggestionId ou matchingOpenOpportunityId.
        - Nunca invente ids. Use null quando nenhum candidato representar o mesmo intuito.
        - Para cada nova atividade ou oportunidade, gere também uma semantic intent key curta, estavel e baseada no significado, nao na frase usada. Intuitos equivalentes devem receber a mesma chave; intuitos diferentes devem receber chaves diferentes.
        - existingSuggestions com status accepted representam acoes recentemente concluidas. Use-as como historico para nao sugerir novamente o que a equipe acabou de realizar.
        - Se o novo trecho contiver apenas a mensagem de saida que cumpriu uma sugestao accepted, sem resposta posterior do cliente, nao crie outra atividade para repetir a cobranca ou aguardar a mesma devolutiva: devolva nextSteps vazio, shouldCreateActivity false e os campos da atividade como null.
        - Uma nova atividade depois de uma sugestao accepted so e valida quando uma mensagem posterior do cliente, um novo compromisso, um novo prazo ou uma mudanca material de contexto estabelecer uma acao realmente distinta.

        Consistencia entre proximos passos e sugestao de atividade:
        - nextSteps deve conter somente acoes concretas e executaveis pela equipe, sustentadas pela conversa.
        - Se nextSteps tiver ao menos um item, shouldCreateActivity deve ser true e activityTitle/activityNotes devem representar esses proximos passos.
        - Se nao existir acao suficientemente clara para sugerir uma atividade, devolva nextSteps vazio, shouldCreateActivity false e os campos da atividade como null.
        - Nunca deixe uma acao executavel apenas em conversationSummary, commercialObservations ou nextSteps.

        Scorecard incremental na mesma chamada:
        - Quando input.scorecardTemplate estiver preenchido, devolva exatamente um scorecardItems para cada criterio recebido.
        - Avalie a conversa comercial do dia usando newTranscript e previousDailyItems como estado acumulado compacto; nao solicite nem reconstrua o historico bruto anterior.
        - Atualize a avaliacao anterior somente quando o novo trecho trouxer evidencia adicional, contraditoria ou mais conclusiva.
        - Evidencias devem ser citacoes literais presentes em newTranscript ou nas evidencias de previousDailyItems. Nunca use previousSummary como evidencia.
        - Considere os horarios das mensagens para cadencia e tempo de resposta, distinguindo Equipe e Cliente.
        - Audios transcritos fazem parte de newTranscript e devem ser avaliados como qualquer outra mensagem.
        - Quando input.scorecardTemplate for null, devolva scorecardItems vazio.
        """;
    private const string WhatsappAttendanceInstructions = """
        Resposta pendente do atendente:
        - Avalie todo o ultimo turno do cliente ainda sem resposta da equipe, em conjunto com previousSummary; nao considere somente a ultima frase. Um emoji, agradecimento ou saudacao depois de um pedido nao resolve esse pedido.
        - requiresSellerResponse deve ser true quando houver pergunta, solicitacao, confirmacao ou continuidade de atendimento pendente nesse turno.
        - Inclua confirmacoes de agenda, pedidos de informacao e perguntas comerciais, mesmo quando nao houver prazo declarado. O envio de placa, documento ou dado solicitado para cotacao exige continuidade pela equipe; nao aguarde outra pergunta do cliente para sugerir esse retorno.
        - Uma saudacao que inicia ou retoma contato e ainda nao foi atendida exige acolhimento e identificacao da demanda: requiresSellerResponse true e uma atividade para iniciar ou retomar o atendimento, mesmo sem oportunidade comercial identificada.
        - Nesses casos, devolva tambem uma atividade concreta para responder ao cliente; nao use o horario futuro do compromisso como prazo para enviar a resposta.
        - Uma sugestao existente deduplica o registro, mas nao atende o cliente: devolva a atividade com activityMatchingSuggestionId e requiresSellerResponse true enquanto houver retorno pendente. Uma cotacao solicitada continua pendente depois que o cliente envia os dados pedidos.
        - requiresSellerResponse deve ser false quando a equipe ja respondeu ou quando o turno inteiro for apenas agradecimento, emoji, despedida ou confirmacao de encerramento sem pendencia. Comunicados automaticos, publicidade e spam sem demanda de atendimento nao exigem resposta.
        """;
    private const string InstagramResponseInstructions = """
        Resposta pendente do atendente:
        - requiresSellerResponse deve ser true quando a ultima mensagem do novo trecho for do cliente e contiver pergunta, solicitacao ou confirmacao que exija resposta da equipe.
        - Inclua confirmacoes de agenda, pedidos de informacao e perguntas comerciais, mesmo quando nao houver prazo declarado.
        - Nesses casos, devolva tambem uma atividade concreta para responder ao cliente; nao use o horario futuro do compromisso como prazo para enviar a resposta.
        - requiresSellerResponse deve ser false quando a equipe ja respondeu depois da pergunta, ou quando a ultima mensagem for apenas agradecimento, emoji, saudacao, despedida ou confirmacao final sem nova acao.
        """;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        WhatsappConversationAnalysisInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = ResolveApiKey(settings);
        var endpoint = configuredOptions.ResponsesEndpoint;
        var startedAt = DateTime.UtcNow;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? configuredOptions.Model : settings.Model;
        var channel = string.Equals(invocationContext.PlatformArea, "instagram", StringComparison.OrdinalIgnoreCase)
            ? "instagram"
            : "whatsapp";
        var operation = $"responses.{channel}-conversation-analysis";
        var responseInstructions = channel == "whatsapp" ? WhatsappAttendanceInstructions : InstagramResponseInstructions;
        var timeZoneInstructions = $"""
            Consistencia obrigatoria de data e fuso horario:
            - O fuso horario da empresa e {settings.TimeZoneId}.
            - Interprete horarios mencionados sem offset, como "as 14h", nesse fuso horario.
            - activityDueAt deve preservar o dia e horario local combinados e incluir o offset UTC explicito do fuso da empresa.
            - Nao use Z para um horario local, exceto quando o fuso da empresa for UTC.
            - Exemplo: 03/09/2026 as 14h em America/Sao_Paulo deve ser 2026-09-03T14:00:00-03:00.
            """;
        var payload = new
        {
            model,
            reasoning = OpenAiGpt56RequestOptions.Reasoning(model, "none"),
            instructions = $"{settings.Instructions}\n\n{SemanticDeduplicationInstructions}\n\n{responseInstructions}\n\n{timeZoneInstructions}\n\n{ParticipantIdentityInstructions}",
            input = JsonSerializer.Serialize(input, SerializerOptions),
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "conversation_analysis_result",
                    strict = true,
                    schema = WhatsappConversationAnalysisJsonSchema.Value
                }
            }
        };
        var requestJson = JsonSerializer.Serialize(payload, SerializerOptions);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var exception = new OpenAiRequestException("OpenAI API key was not configured for this agent. Set ai_agent_settings.api_key.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                operation,
                endpoint,
                invocationContext,
                startedAt,
                null,
                false,
                requestJson,
                null,
                null,
                exception), cancellationToken);
            throw exception;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        string? responseBody = null;
        try
        {
            await invocationLogStore.EnsureCreditsAvailableAsync(invocationContext, cancellationToken);
            response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                operation,
                endpoint,
                invocationContext,
                startedAt,
                null,
                false,
                requestJson,
                null,
                null,
                exception), cancellationToken);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException(
                $"OpenAI {channel} conversation analysis failed with status {(int)response.StatusCode}: {responseBody}",
                (int)response.StatusCode,
                responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                operation,
                endpoint,
                invocationContext,
                startedAt,
                (int)response.StatusCode,
                false,
                requestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody),
                null,
                exception), cancellationToken);
            throw exception;
        }

        string outputText;
        OpenAiWhatsappConversationAnalysisResponse result;
        try
        {
            outputText = ExtractOutputText(responseBody);
            result = JsonSerializer.Deserialize<OpenAiWhatsappConversationAnalysisResponse>(outputText, SerializerOptions)
                ?? throw new InvalidOperationException("OpenAI response did not match the WhatsApp conversation analysis schema.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                operation,
                endpoint,
                invocationContext,
                startedAt,
                (int)response.StatusCode,
                false,
                requestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody),
                null,
                exception), cancellationToken);
            throw;
        }

        await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
            settings,
            configuredOptions.Model,
            operation,
            endpoint,
            invocationContext,
            startedAt,
            (int)response.StatusCode,
            true,
            requestJson,
            OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody),
            OpenAiInvocationLogBuilder.NormalizeJsonBody(outputText)), cancellationToken);

        return result;
    }

    private static string? ResolveApiKey(AiAgentRuntimeSettings settings)
        => FirstConfiguredValue(settings.ApiKey);

    private static string? FirstConfiguredValue(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ExtractOutputText(string responseBody)
    {
        var output = JsonSerializer.Deserialize<OpenAiResponseEnvelope>(responseBody, SerializerOptions)
            ?? throw new InvalidOperationException("OpenAI response was empty.");
        var outputText = output.Output
            .SelectMany(item => item.Content)
            .Where(content => string.Equals(content.Type, "output_text", StringComparison.OrdinalIgnoreCase))
            .Select(content => content.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return outputText ?? throw new InvalidOperationException("OpenAI response did not include output_text.");
    }

    private sealed record OpenAiResponseEnvelope(IReadOnlyCollection<OpenAiOutputItem> Output);

    private sealed record OpenAiOutputItem(IReadOnlyCollection<OpenAiOutputContent> Content);

    private sealed record OpenAiOutputContent(string Type, string? Text);
}
