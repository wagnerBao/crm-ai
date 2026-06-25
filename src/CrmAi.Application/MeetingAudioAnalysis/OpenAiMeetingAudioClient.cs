using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed class OpenAiMeetingAudioClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiMeetingAudioClient
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
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        var apiKey = ResolveApiKey(settings);
        const string endpoint = "https://api.openai.com/v1/audio/transcriptions";
        var startedAt = DateTime.UtcNow;
        var transcriptionModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL") ?? "gpt-4o-mini-transcribe";
        var chunkingStrategy = SupportsAudioChunking(transcriptionModel) ? "auto" : null;
        var requestJson = JsonSerializer.Serialize(new
        {
            model = transcriptionModel,
            language = "pt",
            chunking_strategy = chunkingStrategy,
            file = new
            {
                name = string.IsNullOrWhiteSpace(fileName) ? "meet-audio.webm" : fileName,
                mimeType = NormalizeMimeType(mimeType),
                sizeBytes = content.Length
            }
        }, SerializerOptions);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var exception = new OpenAiRequestException("OpenAI API key was not configured for this agent. Set ai_agent_settings.api_key.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription",
                endpoint,
                invocationContext,
                startedAt,
                null,
                false,
                requestJson,
                null,
                null,
                exception,
                modelOverride: transcriptionModel), cancellationToken);
            throw exception;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(transcriptionModel), "model");
        form.Add(new StringContent("pt"), "language");
        if (!string.IsNullOrWhiteSpace(chunkingStrategy))
        {
            form.Add(new StringContent(chunkingStrategy), "chunking_strategy");
        }

        form.Add(new ByteArrayContent(content)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(NormalizeMimeType(mimeType)) }
        }, "file", string.IsNullOrWhiteSpace(fileName) ? "meet-audio.webm" : fileName);
        request.Content = form;

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
                transcriptionModel,
                "audio.transcription",
                endpoint,
                invocationContext,
                startedAt,
                null,
                false,
                requestJson,
                null,
                null,
                exception,
                modelOverride: transcriptionModel), cancellationToken);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException(
                $"OpenAI audio transcription failed with status {(int)response.StatusCode}: {responseBody}",
                (int)response.StatusCode,
                responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription",
                endpoint,
                invocationContext,
                startedAt,
                (int)response.StatusCode,
                false,
                requestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody),
                null,
                exception,
                modelOverride: transcriptionModel), cancellationToken);
            throw exception;
        }

        string transcript;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            transcript = document.RootElement.TryGetProperty("text", out var textElement)
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription",
                endpoint,
                invocationContext,
                startedAt,
                (int)response.StatusCode,
                false,
                requestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody),
                null,
                exception,
                modelOverride: transcriptionModel), cancellationToken);
            throw;
        }

        await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
            settings,
            transcriptionModel,
            "audio.transcription",
            endpoint,
            invocationContext,
            startedAt,
            (int)response.StatusCode,
            true,
            requestJson,
            OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody),
            JsonSerializer.Serialize(new { text = transcript }, SerializerOptions),
            modelOverride: transcriptionModel), cancellationToken);

        return transcript;
    }

    public async Task<OpenAiMeetingAudioAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        MeetingAudioAnalysisInput input,
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
                    name = "meeting_audio_analysis_result",
                    strict = true,
                    schema = MeetingAudioAnalysisJsonSchema.Value
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
                "responses.meeting-audio-analysis",
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
                "responses.meeting-audio-analysis",
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
                $"OpenAI meeting audio analysis failed with status {(int)response.StatusCode}: {responseBody}",
                (int)response.StatusCode,
                responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                "responses.meeting-audio-analysis",
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
        OpenAiMeetingAudioAnalysisResponse result;
        try
        {
            outputText = ExtractOutputText(responseBody);
            result = JsonSerializer.Deserialize<OpenAiMeetingAudioAnalysisResponse>(outputText, SerializerOptions)
                ?? throw new InvalidOperationException("OpenAI response did not match the meeting audio analysis schema.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                configuredOptions.Model,
                "responses.meeting-audio-analysis",
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
            "responses.meeting-audio-analysis",
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

    private static string NormalizeMimeType(string? mimeType)
    {
        var normalized = mimeType?.Split(';', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "audio/webm" : normalized;
    }

    private static bool SupportsAudioChunking(string model) =>
        model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase)
        && model.Contains("transcribe", StringComparison.OrdinalIgnoreCase);

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
