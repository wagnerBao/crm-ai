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
        required = new[] { "executiveSummary", "items", "trends" },
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
                    required = new[] { "gapKey", "category", "groupId", "title", "justification", "objective", "targetAudience", "priority", "format", "durationMinutes", "outline", "evidenceIds", "recommendedAction", "confidence" },
                    properties = new
                    {
                        gapKey = new { type = "string", @enum = new[] { "follow_up", "qualification", "proposal", "risk", "product", "productivity" } },
                        category = new { type = "string", @enum = new[] { "follow_up", "qualification", "proposal", "risk", "product", "productivity" } },
                        groupId = new { type = "string" },
                        title = new { type = "string", maxLength = 120 },
                        justification = new { type = "string", maxLength = 800 },
                        objective = new { type = "string", maxLength = 600 },
                        targetAudience = new { type = "string", maxLength = 160 },
                        priority = new { type = "string", @enum = new[] { "low", "medium", "high", "critical" } },
                        format = new { type = "string", maxLength = 120 },
                        durationMinutes = new { type = "integer", minimum = 5, maximum = 480 },
                        outline = new { type = "array", minItems = 1, maxItems = 8, items = new { type = "string", maxLength = 300 } },
                        evidenceIds = new { type = "array", minItems = 5, maxItems = 20, items = new { type = "string" } },
                        recommendedAction = new { type = "string", maxLength = 600 },
                        confidence = new { type = "integer", minimum = 0, maximum = 100 }
                    }
                }
            },
            trends = new
            {
                type = "array",
                maxItems = 12,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "gapKey", "category", "groupId", "title", "reason", "evidenceIds", "confidence" },
                    properties = new
                    {
                        gapKey = new { type = "string", @enum = new[] { "follow_up", "qualification", "proposal", "risk", "product", "productivity" } },
                        category = new { type = "string", @enum = new[] { "follow_up", "qualification", "proposal", "risk", "product", "productivity" } },
                        groupId = new { type = "string" },
                        title = new { type = "string", maxLength = 120 },
                        reason = new { type = "string", maxLength = 300 },
                        evidenceIds = new { type = "array", maxItems = 20, items = new { type = "string" } },
                        confidence = new { type = "integer", minimum = 0, maximum = 100 }
                    }
                }
            }
        }
    };
}

public sealed record CoachSynthesisResult(string ExecutiveSummary, IReadOnlyCollection<CoachSynthesisItem> Items, IReadOnlyCollection<CoachSynthesisTrend> Trends);
public sealed record CoachSynthesisItem(
    string GapKey,
    string Category,
    string GroupId,
    string Title,
    string Justification,
    string Objective,
    string TargetAudience,
    string Priority,
    string Format,
    int DurationMinutes,
    IReadOnlyCollection<string> Outline,
    IReadOnlyCollection<string> EvidenceIds,
    string RecommendedAction,
    int Confidence);
public sealed record CoachSynthesisTrend(string GapKey, string Category, string GroupId, string Title, string Reason, IReadOnlyCollection<string> EvidenceIds, int Confidence);
