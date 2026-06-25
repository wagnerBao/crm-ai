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
        var requestJson = BuildTranscriptionRequestJson(transcriptionModel, fileName, mimeType, content.Length, null);

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

        var attempt = await SendTranscriptionAttemptAsync(
            apiKey,
            endpoint,
            transcriptionModel,
            fileName,
            mimeType,
            content,
            null,
            cancellationToken);

        if (!attempt.IsSuccessStatusCode && SupportsAudioChunking(transcriptionModel) && IsInputTooLarge(attempt.ResponseBody))
        {
            var exception = new OpenAiRequestException(
                $"OpenAI audio transcription failed with status {attempt.StatusCode}: {attempt.ResponseBody}",
                attempt.StatusCode,
                attempt.ResponseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription",
                endpoint,
                invocationContext,
                attempt.StartedAt,
                attempt.StatusCode,
                false,
                attempt.RequestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(attempt.ResponseBody),
                null,
                exception,
                modelOverride: transcriptionModel), cancellationToken);

            attempt = await SendTranscriptionAttemptAsync(
                apiKey,
                endpoint,
                transcriptionModel,
                fileName,
                mimeType,
                content,
                "auto",
                cancellationToken);
        }

        if (!attempt.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException(
                $"OpenAI audio transcription failed with status {attempt.StatusCode}: {attempt.ResponseBody}",
                attempt.StatusCode,
                attempt.ResponseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription",
                endpoint,
                invocationContext,
                attempt.StartedAt,
                attempt.StatusCode,
                false,
                attempt.RequestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(attempt.ResponseBody),
                null,
                exception,
                modelOverride: transcriptionModel), cancellationToken);
            throw exception;
        }

        string transcript;
        try
        {
            using var document = JsonDocument.Parse(attempt.ResponseBody);
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
                attempt.StartedAt,
                attempt.StatusCode,
                false,
                attempt.RequestJson,
                OpenAiInvocationLogBuilder.NormalizeJsonBody(attempt.ResponseBody),
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
            attempt.StartedAt,
            attempt.StatusCode,
            true,
            attempt.RequestJson,
            OpenAiInvocationLogBuilder.NormalizeJsonBody(attempt.ResponseBody),
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

    private async Task<TranscriptionAttempt> SendTranscriptionAttemptAsync(
        string apiKey,
        string endpoint,
        string transcriptionModel,
        string fileName,
        string mimeType,
        byte[] content,
        string? chunkingStrategy,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var requestJson = BuildTranscriptionRequestJson(transcriptionModel, fileName, mimeType, content.Length, chunkingStrategy);
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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new TranscriptionAttempt(startedAt, (int)response.StatusCode, response.IsSuccessStatusCode, requestJson, responseBody);
    }

    private static string BuildTranscriptionRequestJson(string transcriptionModel, string fileName, string mimeType, int sizeBytes, string? chunkingStrategy) =>
        JsonSerializer.Serialize(new
        {
            model = transcriptionModel,
            language = "pt",
            chunking_strategy = chunkingStrategy,
            file = new
            {
                name = string.IsNullOrWhiteSpace(fileName) ? "meet-audio.webm" : fileName,
                mimeType = NormalizeMimeType(mimeType),
                sizeBytes
            }
        }, SerializerOptions);

    private static bool SupportsAudioChunking(string model) =>
        model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase)
        && model.Contains("transcribe", StringComparison.OrdinalIgnoreCase);

    private static bool IsInputTooLarge(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        if (responseBody.Contains("input_too_large", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.GetString()?.Contains("too large", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

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

    private sealed record TranscriptionAttempt(DateTime StartedAt, int StatusCode, bool IsSuccessStatusCode, string RequestJson, string ResponseBody);
}
