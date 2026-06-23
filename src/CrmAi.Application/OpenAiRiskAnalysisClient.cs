using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiRiskAnalysisOptions
{
    public const string SectionName = "OpenAI";

    public string Model { get; init; } = "gpt-4.1-mini";

    public string ResponsesEndpoint { get; init; } = "https://api.openai.com/v1/responses";
}

public sealed class OpenAiResponsesRiskAnalysisClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiRiskAnalysisClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAiRiskAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        RiskAnalysisAgentInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = ResolveApiKey(settings);
        var endpoint = configuredOptions.ResponsesEndpoint;
        var startedAt = DateTime.UtcNow;
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? configuredOptions.Model : settings.Model,
            instructions = settings.Instructions,
            input = JsonSerializer.Serialize(input, SerializerOptions),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "risk_analysis_result",
                    strict = true,
                    schema = RiskAnalysisJsonSchema.Value
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
                "responses.risk-analysis",
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
                "responses.risk-analysis",
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
                $"OpenAI risk analysis failed with status {(int)response.StatusCode}: {responseBody}",
                (int)response.StatusCode,
                responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                "responses.risk-analysis",
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
        OpenAiRiskAnalysisResponse result;
        try
        {
            outputText = ExtractOutputText(responseBody);
            result = JsonSerializer.Deserialize<OpenAiRiskAnalysisResponse>(outputText, SerializerOptions)
                ?? throw new InvalidOperationException("OpenAI response did not match the risk analysis schema.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                "responses.risk-analysis",
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
            "responses.risk-analysis",
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
