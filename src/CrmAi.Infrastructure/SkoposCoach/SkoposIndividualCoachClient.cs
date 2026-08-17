using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrmAi.Application;
using Microsoft.Extensions.Options;

namespace CrmAi.Infrastructure.SkoposCoach;

public sealed class SkoposIndividualCoachClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IndividualCoachResult?> AnalyzeAsync(AiAgentRuntimeSettings settings, object input, string companyId, CancellationToken cancellationToken)
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
            text = new { format = new { type = "json_schema", name = "skopos_individual_pdi", strict = true, schema = Schema } }
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
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI Individual Coach returned HTTP {(int)response.StatusCode}.");
            var outputText = ExtractOutputText(responseBody);
            var result = JsonSerializer.Deserialize<IndividualCoachResult>(outputText, JsonOptions) ?? throw new InvalidOperationException("Individual Coach response was empty.");
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
            Guid.NewGuid(), settings.AgentKey, settings.Provider, model, "responses.skopos-individual-coach", "skopos-individual-coach", endpoint,
            status, success, success ? "success" : "error", request, response, result, exception?.GetType().Name,
            exception?.Message, new(null, null, null, null, null), new("skopos-individual-coach", CompanyId: companyId, ContextEntityKeys: settings.ContextEntityKeys),
            startedAt, DateTime.UtcNow), cancellationToken);
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
            foreach (var content in output.GetProperty("content").EnumerateArray())
                if (content.TryGetProperty("type", out var type) && type.GetString() == "output_text" && content.TryGetProperty("text", out var text)) return text.GetString() ?? "{}";
        throw new InvalidOperationException("Individual Coach did not return output_text.");
    }

    private static readonly object Schema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "summary", "objective", "strengths", "items" },
        properties = new
        {
            summary = new { type = "string", maxLength = 700 },
            objective = new { type = "string", maxLength = 400 },
            strengths = new
            {
                type = "array", maxItems = 3,
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "key", "title", "summary", "score" },
                    properties = new { key = CompetencyKey, title = ShortText, summary = LongText, score = Score }
                }
            },
            items = new
            {
                type = "array", minItems = 1, maxItems = 3,
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "competencyKey", "title", "baselineScore", "targetScore", "action", "measurement", "resource", "dueInDays", "evidenceIds" },
                    properties = new
                    {
                        competencyKey = CompetencyKey,
                        title = ShortText,
                        baselineScore = Score,
                        targetScore = Score,
                        action = LongText,
                        measurement = LongText,
                        resource = LongText,
                        dueInDays = new { type = "integer", minimum = 7, maximum = 90 },
                        evidenceIds = new { type = "array", maxItems = 5, items = new { type = "string" } }
                    }
                }
            }
        }
    };
    private static readonly object CompetencyKey = new { type = "string", @enum = new[] { "service", "cadence", "qualification", "objections", "proposal", "product", "execution" } };
    private static readonly object ShortText = new { type = "string", maxLength = 140 };
    private static readonly object LongText = new { type = "string", maxLength = 500 };
    private static readonly object Score = new { type = "integer", minimum = 0, maximum = 100 };
}

public sealed record IndividualCoachResult(string Summary, string Objective, IReadOnlyCollection<IndividualCoachStrength> Strengths, IReadOnlyCollection<IndividualCoachItem> Items);
public sealed record IndividualCoachStrength(string Key, string Title, string Summary, int Score);
public sealed record IndividualCoachItem(string CompetencyKey, string Title, int BaselineScore, int TargetScore, string Action, string Measurement, string Resource, int DueInDays, IReadOnlyCollection<string> EvidenceIds);
