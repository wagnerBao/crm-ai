using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiRiskAnalysisOptions
{
    public const string SectionName = "OpenAI";

    public string? ApiKey { get; init; }

    public string Model { get; init; } = "gpt-4.1-mini";

    public string ResponsesEndpoint { get; init; } = "https://api.openai.com/v1/responses";
}

public sealed class OpenAiResponsesRiskAnalysisClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options) : IOpenAiRiskAnalysisClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAiRiskAnalysisResponse> AnalyzeAsync(
        string instructions,
        RiskAnalysisAgentInput input,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = ResolveApiKey(configuredOptions);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key was not configured. Set OpenAI:ApiKey or OPENAI_API_KEY.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuredOptions.ResponsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = configuredOptions.Model,
            instructions,
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
        }, options: SerializerOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI risk analysis failed with status {(int)response.StatusCode}: {responseBody}");
        }

        var outputText = ExtractOutputText(responseBody);
        return JsonSerializer.Deserialize<OpenAiRiskAnalysisResponse>(outputText, SerializerOptions)
            ?? throw new InvalidOperationException("OpenAI response did not match the risk analysis schema.");
    }

    private static string? ResolveApiKey(OpenAiRiskAnalysisOptions configuredOptions)
        => FirstConfiguredValue(
            configuredOptions.ApiKey,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Machine));

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
