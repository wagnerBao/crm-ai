using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrmAi.Domain;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiResponsesDailyCheckoutClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiDailyCheckoutClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAiDailyCheckoutResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        DailyCheckoutAnalysisInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? null : settings.ApiKey.Trim();
        var endpoint = configuredOptions.ResponsesEndpoint;
        var startedAt = DateTime.UtcNow;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? configuredOptions.Model : settings.Model;
        var payload = new
        {
            model,
            reasoning = OpenAiGpt56RequestOptions.Reasoning(model, "low"),
            instructions = settings.Instructions,
            input = JsonSerializer.Serialize(input, SerializerOptions),
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "daily_checkout_result",
                    strict = true,
                    schema = DailyCheckoutJsonSchema.Value
                }
            }
        };
        var requestJson = JsonSerializer.Serialize(payload, SerializerOptions);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var exception = new OpenAiRequestException("OpenAI API key was not configured for this agent. Set ai_agent_settings.api_key.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configuredOptions.Model, "responses.daily-checkout", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
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
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configuredOptions.Model, "responses.daily-checkout", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException($"OpenAI daily checkout failed with status {(int)response.StatusCode}: {responseBody}", (int)response.StatusCode, responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configuredOptions.Model, "responses.daily-checkout", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw exception;
        }

        string outputText;
        OpenAiDailyCheckoutResponse result;
        try
        {
            outputText = ExtractOutputText(responseBody ?? "");
            result = JsonSerializer.Deserialize<OpenAiDailyCheckoutResponse>(outputText, SerializerOptions)
                ?? throw new InvalidOperationException("OpenAI response did not match the daily checkout schema.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configuredOptions.Model, "responses.daily-checkout", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw;
        }

        await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configuredOptions.Model, "responses.daily-checkout", endpoint, invocationContext, startedAt, (int)response.StatusCode, true, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), OpenAiInvocationLogBuilder.NormalizeJsonBody(outputText)), cancellationToken);
        return result;
    }

    private static string ExtractOutputText(string responseBody)
    {
        var output = JsonSerializer.Deserialize<OpenAiResponseEnvelope>(responseBody, SerializerOptions)
            ?? throw new InvalidOperationException("OpenAI response was empty.");
        return output.Output
            .SelectMany(item => item.Content)
            .Where(content => string.Equals(content.Type, "output_text", StringComparison.OrdinalIgnoreCase))
            .Select(content => content.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?? throw new InvalidOperationException("OpenAI response did not include output_text.");
    }

    private sealed record OpenAiResponseEnvelope(IReadOnlyCollection<OpenAiOutputItem> Output);
    private sealed record OpenAiOutputItem(IReadOnlyCollection<OpenAiOutputContent> Content);
    private sealed record OpenAiOutputContent(string Type, string? Text);
}
