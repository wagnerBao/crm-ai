using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed record SuggestionCompletionEvidence(
    string Id,
    string Type,
    DateTime OccurredAt,
    bool BeforeSuggestion,
    string Summary);

public sealed record SuggestionCompletionVerificationInput(
    string SuggestionId,
    string SuggestionType,
    string Title,
    string Description,
    DateTime CreatedAt,
    DateTime DueAt,
    JsonElement Payload,
    IReadOnlyCollection<SuggestionCompletionEvidence> Evidence);

public sealed record SuggestionCompletionVerificationResult(
    string Result,
    int Confidence,
    string Reason,
    IReadOnlyCollection<string> EvidenceIds);

public interface IOpenAiSuggestionCompletionVerificationClient
{
    Task<SuggestionCompletionVerificationResult> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        SuggestionCompletionVerificationInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

internal static class SuggestionCompletionVerificationJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "result", "confidence", "reason", "evidenceIds" },
        properties = new
        {
            result = new { type = "string", @enum = new[] { "fulfilled", "unfulfilled", "inconclusive" } },
            confidence = new { type = "integer", minimum = 0, maximum = 100 },
            reason = new { type = "string" },
            evidenceIds = new { type = "array", items = new { type = "string" } }
        }
    };
}

public sealed class OpenAiSuggestionCompletionVerificationClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiSuggestionCompletionVerificationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SuggestionCompletionVerificationResult> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        SuggestionCompletionVerificationInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var endpoint = configured.ResponsesEndpoint;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? configured.Model : settings.Model;
        var startedAt = DateTime.UtcNow;
        var payload = new
        {
            model,
            reasoning = OpenAiGpt56RequestOptions.Reasoning(model, "low"),
            instructions = settings.Instructions,
            input = JsonSerializer.Serialize(input, JsonOptions),
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "suggestion_completion_verification",
                    strict = true,
                    schema = SuggestionCompletionVerificationJsonSchema.Value
                }
            }
        };
        var requestJson = JsonSerializer.Serialize(payload, JsonOptions);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var exception = new OpenAiRequestException("OpenAI API key was not configured for suggestion-completion-verification.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
            throw exception;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException($"OpenAI suggestion completion verification failed with status {(int)response.StatusCode}.", (int)response.StatusCode, responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw exception;
        }

        try
        {
            var outputText = ExtractOutputText(responseBody);
            var result = JsonSerializer.Deserialize<SuggestionCompletionVerificationResult>(outputText, JsonOptions)
                ?? throw new InvalidOperationException("OpenAI response did not match suggestion completion verification schema.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, (int)response.StatusCode, true, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), OpenAiInvocationLogBuilder.NormalizeJsonBody(outputText)), cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw;
        }
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
        foreach (var content in output.GetProperty("content").EnumerateArray())
        {
            if (content.TryGetProperty("type", out var type) && type.GetString() == "output_text"
                && content.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return text.GetString()!;
            }
        }
        throw new InvalidOperationException("OpenAI response did not include output_text.");
    }
}
