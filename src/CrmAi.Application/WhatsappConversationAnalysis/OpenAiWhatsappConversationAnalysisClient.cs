using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiResponsesWhatsappConversationAnalysisClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options) : IOpenAiWhatsappConversationAnalysisClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        WhatsappConversationAnalysisInput input,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = ResolveApiKey(settings, configuredOptions);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key was not configured. Set OpenAI:ApiKey or OPENAI_API_KEY.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuredOptions.ResponsesEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? configuredOptions.Model : settings.Model,
            instructions = settings.Instructions,
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
        }, options: SerializerOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI WhatsApp conversation analysis failed with status {(int)response.StatusCode}: {responseBody}");
        }

        var outputText = ExtractOutputText(responseBody);
        return JsonSerializer.Deserialize<OpenAiWhatsappConversationAnalysisResponse>(outputText, SerializerOptions)
            ?? throw new InvalidOperationException("OpenAI response did not match the WhatsApp conversation analysis schema.");
    }

    private static string? ResolveApiKey(AiAgentRuntimeSettings settings, OpenAiRiskAnalysisOptions configuredOptions)
        => FirstConfiguredValue(
            settings.ApiKey,
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
