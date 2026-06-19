using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiMeetingAudioClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options) : IOpenAiMeetingAudioClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> TranscribeAsync(
        AiAgentRuntimeSettings settings,
        string fileName,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = ResolveApiKey(settings, configuredOptions);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key was not configured. Set OpenAI:ApiKey or OPENAI_API_KEY.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL") ?? "gpt-4o-mini-transcribe"), "model");
        form.Add(new StringContent("pt"), "language");
        form.Add(new ByteArrayContent(content)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "audio/webm" : mimeType) }
        }, "file", string.IsNullOrWhiteSpace(fileName) ? "meet-audio.webm" : fileName);
        request.Content = form;

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI audio transcription failed with status {(int)response.StatusCode}: {responseBody}");
        }

        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.TryGetProperty("text", out var textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
    }

    public async Task<OpenAiMeetingAudioAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        MeetingAudioAnalysisInput input,
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
                    name = "meeting_audio_analysis_result",
                    strict = true,
                    schema = MeetingAudioAnalysisJsonSchema.Value
                }
            }
        }, options: SerializerOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI meeting audio analysis failed with status {(int)response.StatusCode}: {responseBody}");
        }

        var outputText = ExtractOutputText(responseBody);
        return JsonSerializer.Deserialize<OpenAiMeetingAudioAnalysisResponse>(outputText, SerializerOptions)
            ?? throw new InvalidOperationException("OpenAI response did not match the meeting audio analysis schema.");
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
