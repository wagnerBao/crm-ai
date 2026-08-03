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
    private const string SemanticDeduplicationInstructions = """
        Deduplicacao semantica obrigatoria:
        - Compare cada atividade ou oportunidade sugerida com existingSuggestions e existingOpenOpportunities.
        - A decisao deve considerar o intuito comercial, a acao esperada, o objeto/produto/veiculo, o problema tratado e o resultado pretendido; nao use apenas igualdade textual.
        - Mudanca de redacao, detalhe, prazo ou data de uma mesma pendencia continua sendo o mesmo intuito e deve atualizar a sugestao existente.
        - Produtos, veiculos, necessidades, problemas ou resultados comerciais materialmente diferentes representam intuitos distintos e podem gerar novos registros.
        - Quando houver o mesmo intuito, retorne exatamente o id do candidato em activityMatchingSuggestionId, opportunityMatchingSuggestionId ou matchingOpenOpportunityId.
        - Nunca invente ids. Use null quando nenhum candidato representar o mesmo intuito.
        - Para cada nova atividade ou oportunidade, gere também uma semantic intent key curta, estavel e baseada no significado, nao na frase usada. Intuitos equivalentes devem receber a mesma chave; intuitos diferentes devem receber chaves diferentes.
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
        var payload = new
        {
            model,
            reasoning = OpenAiGpt56RequestOptions.Reasoning(model, "none"),
            instructions = $"{settings.Instructions}\n\n{SemanticDeduplicationInstructions}",
            input = JsonSerializer.Serialize(input, SerializerOptions),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "whatsapp_conversation_analysis_result",
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
                "responses.whatsapp-conversation-analysis",
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
            response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                "responses.whatsapp-conversation-analysis",
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
                $"OpenAI WhatsApp conversation analysis failed with status {(int)response.StatusCode}: {responseBody}",
                (int)response.StatusCode,
                responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                "responses.whatsapp-conversation-analysis",
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
                "responses.whatsapp-conversation-analysis",
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
            "responses.whatsapp-conversation-analysis",
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
