using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrmAi.Application;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.SkoposCoach;

public sealed class SkoposCoachSynthesisClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CoachSynthesisResult?> AnalyzeAsync(AiAgentRuntimeSettings settings, object input, string companyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return null;
        var endpoint = options.Value.ResponsesEndpoint;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? options.Value.Model : settings.Model;
        var payload = new
        {
            model,
            reasoning = new { effort = "low" },
            instructions = settings.Instructions,
            input = JsonSerializer.Serialize(input, JsonOptions),
            text = new { format = new { type = "json_schema", name = "skopos_coach_result", strict = true, schema = Schema } }
        };
        var requestJson = JsonSerializer.Serialize(payload, JsonOptions);
        var startedAt = DateTime.UtcNow;
        string? responseBody = null;
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI Coach synthesis returned HTTP {(int)response.StatusCode}.");
            var outputText = ExtractOutputText(responseBody);
            var result = JsonSerializer.Deserialize<CoachSynthesisResult>(outputText, JsonOptions) ?? throw new InvalidOperationException("Coach synthesis response was empty.");
            await SaveLogAsync(settings, model, endpoint, companyId, startedAt, (int)response.StatusCode, true, requestJson, responseBody, outputText, null, cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SaveLogAsync(settings, model, endpoint, companyId, startedAt, response is null ? null : (int)response.StatusCode, false, requestJson, responseBody, null, exception, cancellationToken);
            throw;
        }
    }

    private async Task SaveLogAsync(AiAgentRuntimeSettings settings, string model, string endpoint, string companyId, DateTime startedAt, int? status, bool success, string request, string? response, string? result, Exception? exception, CancellationToken cancellationToken)
    {
        await invocationLogStore.SaveBestEffortAsync(new AiAgentInvocationLogEntry(
            Guid.NewGuid(), settings.AgentKey, settings.Provider, model, "responses.skopos-coach", "skopos-coach", endpoint,
            status, success, success ? "success" : "error", request, response, result, exception?.GetType().Name,
            exception?.Message, new(null, null, null, null, null), new("skopos-coach", CompanyId: companyId, ContextEntityKeys: settings.ContextEntityKeys),
            startedAt, DateTime.UtcNow), cancellationToken);
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
            foreach (var content in output.GetProperty("content").EnumerateArray())
                if (content.TryGetProperty("type", out var type) && type.GetString() == "output_text" && content.TryGetProperty("text", out var text)) return text.GetString() ?? "{}";
        throw new InvalidOperationException("Coach synthesis did not return output_text.");
    }

    private static readonly object Schema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "executiveSummary", "items" },
        properties = new
        {
            executiveSummary = new { type = "string", maxLength = 600 },
            items = new
            {
                type = "array",
                maxItems = 6,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "category", "title", "summary", "recommendedAction", "confidence" },
                    properties = new
                    {
                        category = new { type = "string", @enum = new[] { "follow_up", "qualification", "proposal", "risk", "product", "productivity" } },
                        title = new { type = "string", maxLength = 120 },
                        summary = new { type = "string", maxLength = 500 },
                        recommendedAction = new { type = "string", maxLength = 500 },
                        confidence = new { type = "integer", minimum = 0, maximum = 100 }
                    }
                }
            }
        }
    };
}

public sealed record CoachSynthesisResult(string ExecutiveSummary, IReadOnlyCollection<CoachSynthesisItem> Items);
public sealed record CoachSynthesisItem(string Category, string Title, string Summary, string RecommendedAction, int Confidence);
