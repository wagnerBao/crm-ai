using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed record SuggestionQualityFeedbackEvidence(
    string Id,
    string Sentiment,
    string Action,
    double SignalStrength,
    string Timeliness,
    string? Reason,
    JsonElement Suggestion);

public sealed record SuggestionQualityAuditInput(
    JsonElement Metrics,
    JsonElement Filters,
    bool LowSample,
    string FeedbackScoringGuidance,
    string EvaluatedAgentKey,
    string EvaluatedAgentModel,
    string EvaluatedAgentPrompt,
    string? EvaluatedAgentContextInstructions,
    IReadOnlyCollection<SuggestionQualityFeedbackEvidence> Feedbacks);

public sealed record SuggestionQualityAuditObservation(
    string Title,
    string Finding,
    IReadOnlyCollection<string> EvidenceIds,
    string Severity,
    string Area,
    string Recommendation);

public sealed record SuggestionQualityAuditResult(
    string ExecutiveSummary,
    IReadOnlyCollection<string> SampleLimitations,
    IReadOnlyCollection<SuggestionQualityAuditObservation> Observations);

public interface IOpenAiSuggestionQualityAuditClient
{
    Task<SuggestionQualityAuditResult> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        SuggestionQualityAuditInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

internal static class SuggestionQualityAuditJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "executiveSummary", "sampleLimitations", "observations" },
        properties = new
        {
            executiveSummary = new { type = "string" },
            sampleLimitations = new { type = "array", items = new { type = "string" } },
            observations = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "title", "finding", "evidenceIds", "severity", "area", "recommendation" },
                    properties = new
                    {
                        title = new { type = "string" },
                        finding = new { type = "string" },
                        evidenceIds = new { type = "array", items = new { type = "string" } },
                        severity = new { type = "string", @enum = new[] { "low", "medium", "high", "critical" } },
                        area = new { type = "string", @enum = new[] { "prompt", "context", "timing", "deduplication", "logic", "ux" } },
                        recommendation = new { type = "string" }
                    }
                }
            }
        }
    };
}

public sealed class OpenAiSuggestionQualityAuditClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiSuggestionQualityAuditClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SuggestionQualityAuditResult> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        SuggestionQualityAuditInput input,
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
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "suggestion_quality_audit",
                    strict = true,
                    schema = SuggestionQualityAuditJsonSchema.Value
                }
            }
        };
        var requestJson = JsonSerializer.Serialize(payload, JsonOptions);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var exception = new OpenAiRequestException("OpenAI API key was not configured for suggestion-quality-audit.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-quality-audit", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
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
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-quality-audit", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException($"OpenAI suggestion quality audit failed with status {(int)response.StatusCode}.", (int)response.StatusCode, responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-quality-audit", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw exception;
        }

        try
        {
            var outputText = ExtractOutputText(responseBody);
            var result = JsonSerializer.Deserialize<SuggestionQualityAuditResult>(outputText, JsonOptions)
                ?? throw new InvalidOperationException("OpenAI response did not match suggestion quality audit schema.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-quality-audit", endpoint, invocationContext, startedAt, (int)response.StatusCode, true, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), OpenAiInvocationLogBuilder.NormalizeJsonBody(outputText)), cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-quality-audit", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
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
