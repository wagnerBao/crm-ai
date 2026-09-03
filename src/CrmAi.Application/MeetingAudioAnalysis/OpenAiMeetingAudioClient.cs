using System.Net.Http.Headers;
using System.Diagnostics;
using System.Globalization;
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
    private const string DefaultTranscriptionModel = "gpt-4o-transcribe-diarize";
    private const string DefaultFallbackTranscriptionModel = "gpt-4o-transcribe";
    private const int MaxOpenAiAudioUploadBytes = 24 * 1024 * 1024;
    private const int DefaultSegmentSeconds = 600;
    private const string ActivitySuggestionInstructions = """
        Para reunioes e ligacoes, avalie se existe uma acao comercial concreta sustentada pelo que foi dito.
        Marque shouldCreateActivity=true somente quando houver retorno, envio, cobranca, validacao, agendamento,
        feedback ou outro proximo passo executavel. Preencha activityTitle e activityNotes de forma objetiva.
        Nao crie atividade para conversa social, encerramento generico ou acao sem evidencia na interacao.
        Use activityDueAt somente quando houver prazo claro; caso contrario retorne null.
        """;
    private const string ScorecardInstructions = """
        Quando input.scorecardTemplate estiver preenchido, avalie exatamente cada criterio recebido e devolva um
        scorecardItems para cada criterionKey. Baseie nota, justificativa e recomendacao somente na transcricao e no
        contexto fornecido. Toda evidencia deve ser um trecho literal curto da transcricao. Nao invente participante
        ou timestamp: use null quando indisponivel. Quando um criterio nao tiver cobertura, use baixa confianca,
        explique a ausencia de evidencia e nao presuma desempenho ruim. Quando nao houver template, devolva array vazio.
        """;
    private const string TagSuggestionInstructions = """
        Quando input.availableTags estiver preenchido, sugira no maximo cinco tags exclusivamente dessa lista.
        Use somente o id recebido em tagId e apenas quando houver evidencia literal curta na transcricao.
        Nao invente tags, nao devolva nomes no lugar de ids e nao sugira tags sem relacao comercial verificavel.
        Quando nenhuma tag existente for adequada, devolva suggestedTags como array vazio.
        """;
    private const string ContactFieldSuggestionInstructions = """
        Quando input.availableContactFields estiver preenchido, sugira no maximo cinco atualizacoes exclusivamente
        para os fieldId recebidos. Use somente informacao comercial explicitamente sustentada por um trecho literal
        curto da transcricao e nao sugira o valor que ja estiver em currentValue. Nunca sugira nome, email, telefone,
        responsavel, status, consentimento ou qualquer campo que nao esteja na lista. Para number use decimal invariante,
        para date use YYYY-MM-DD e para boolean use true ou false. Quando options estiver preenchido, use exatamente uma
        das opcoes recebidas. Nao use valor vazio. Sem atualizacao segura, devolva suggestedContactFields como array vazio.
        """;

    public async Task<MeetingAudioTranscriptionResult> TranscribeAsync(
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
        var transcriptionModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL") ?? DefaultTranscriptionModel;
        var fallbackTranscriptionModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL") ?? DefaultFallbackTranscriptionModel;
        var requestJson = BuildTranscriptionRequestJson(transcriptionModel, fileName, mimeType, content.Length, RequiresDiarization(transcriptionModel) ? "auto" : null);

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

        if (content.Length > MaxOpenAiAudioUploadBytes)
        {
            return await TranscribeSegmentedAudioWithLoggingAsync(
                settings,
                apiKey,
                endpoint,
                transcriptionModel,
                fileName,
                mimeType,
                content,
                invocationContext,
                startedAt,
                cancellationToken);
        }

        var attempt = await SendTranscriptionAttemptAsync(
            apiKey,
            endpoint,
            transcriptionModel,
            fileName,
            mimeType,
            content,
            RequiresDiarization(transcriptionModel) ? "auto" : null,
            cancellationToken);

        if (!attempt.IsSuccessStatusCode && !RequiresDiarization(transcriptionModel) && SupportsAudioChunking(transcriptionModel) && IsInputTooLarge(attempt.ResponseBody))
        {
            await LogTranscriptionFailureAsync(settings, transcriptionModel, endpoint, invocationContext, attempt, cancellationToken);

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

        if (!attempt.IsSuccessStatusCode
            && !IsAudioDurationLimitFailure(attempt.ResponseBody)
            && (IsInputTooLarge(attempt.ResponseBody) || CanFallbackFromDiarization(transcriptionModel, attempt.StatusCode))
            && !string.Equals(fallbackTranscriptionModel, transcriptionModel, StringComparison.OrdinalIgnoreCase))
        {
            await LogTranscriptionFailureAsync(settings, transcriptionModel, endpoint, invocationContext, attempt, cancellationToken);

            attempt = await SendTranscriptionAttemptAsync(
                apiKey,
                endpoint,
                fallbackTranscriptionModel,
                fileName,
                mimeType,
                content,
                null,
                cancellationToken);
            transcriptionModel = fallbackTranscriptionModel;
        }

        if (!attempt.IsSuccessStatusCode && IsLargeAudioFailure(attempt.ResponseBody))
        {
            await LogTranscriptionFailureAsync(settings, transcriptionModel, endpoint, invocationContext, attempt, cancellationToken);
            return await TranscribeSegmentedAudioWithLoggingAsync(
                settings,
                apiKey,
                endpoint,
                transcriptionModel,
                fileName,
                mimeType,
                content,
                invocationContext,
                startedAt,
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

        MeetingAudioTranscriptionResult transcript;
        try
        {
            transcript = ParseTranscriptionResponse(attempt.ResponseBody, transcriptionModel).Result;
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
            JsonSerializer.Serialize(new { text = transcript.Text, segments = transcript.Segments.Count }, SerializerOptions),
            modelOverride: transcriptionModel), cancellationToken);

        return transcript;
    }

    private async Task LogTranscriptionFailureAsync(
        AiAgentRuntimeSettings settings,
        string transcriptionModel,
        string endpoint,
        AiAgentInvocationContext invocationContext,
        TranscriptionAttempt attempt,
        CancellationToken cancellationToken)
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
    }

    private async Task<MeetingAudioTranscriptionResult> TranscribeSegmentedAudioWithLoggingAsync(
        AiAgentRuntimeSettings settings,
        string apiKey,
        string endpoint,
        string transcriptionModel,
        string fileName,
        string mimeType,
        byte[] content,
        AiAgentInvocationContext invocationContext,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var requestJson = BuildTranscriptionRequestJson(
            transcriptionModel,
            fileName,
            mimeType,
            content.Length,
            "ffmpeg_segment");

        try
        {
            var segmentedTranscript = await TranscribeSegmentedAudioAsync(
                apiKey,
                endpoint,
                transcriptionModel,
                fileName,
                mimeType,
                content,
                cancellationToken);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription.segmented",
                endpoint,
                invocationContext,
                startedAt,
                200,
                true,
                requestJson,
                null,
                JsonSerializer.Serialize(new { text = segmentedTranscript.Text, segments = segmentedTranscript.Segments.Count }, SerializerOptions),
                modelOverride: transcriptionModel), cancellationToken);
            return segmentedTranscript;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(
                settings,
                transcriptionModel,
                "audio.transcription.segmented",
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
        var model = string.IsNullOrWhiteSpace(settings.Model) ? configuredOptions.Model : settings.Model;
        var isWhatsappCall = string.Equals(settings.AgentKey, "call-audio-analysis", StringComparison.OrdinalIgnoreCase);
        var payload = new
        {
            model,
            reasoning = OpenAiGpt56RequestOptions.Reasoning(model, "low"),
            instructions = string.Join("\n\n", settings.Instructions, ActivitySuggestionInstructions, TagSuggestionInstructions, ContactFieldSuggestionInstructions, ScorecardInstructions),
            input = JsonSerializer.Serialize(input, SerializerOptions),
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = isWhatsappCall ? "call_audio_analysis_result" : "meeting_audio_analysis_result",
                    strict = true,
                    schema = isWhatsappCall ? CallAudioAnalysisJsonSchema.Value : MeetingAudioAnalysisJsonSchema.Value
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
        if (RequiresDiarization(transcriptionModel))
        {
            form.Add(new StringContent("diarized_json"), "response_format");
        }
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

    private async Task<MeetingAudioTranscriptionResult> TranscribeSegmentedAudioAsync(
        string apiKey,
        string endpoint,
        string transcriptionModel,
        string fileName,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var chunks = await SplitAudioWithFfmpegAsync(fileName, mimeType, content, cancellationToken);
        var transcripts = new List<string>();
        var segments = new List<MeetingAudioTranscriptionSegment>();
        var speakerLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var elapsedMs = 0;
        var chunkIndex = 0;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = await SendTranscriptionAttemptAsync(
                apiKey,
                endpoint,
                transcriptionModel,
                chunk.FileName,
                chunk.MimeType,
                chunk.Content,
                RequiresDiarization(transcriptionModel) ? "auto" : null,
                cancellationToken);

            if (!attempt.IsSuccessStatusCode && SupportsAudioChunking(transcriptionModel) && IsInputTooLarge(attempt.ResponseBody))
            {
                attempt = await SendTranscriptionAttemptAsync(
                    apiKey,
                    endpoint,
                    transcriptionModel,
                    chunk.FileName,
                    chunk.MimeType,
                    chunk.Content,
                    "auto",
                    cancellationToken);
            }

            if (!attempt.IsSuccessStatusCode)
            {
                throw new OpenAiRequestException(
                    $"OpenAI segmented audio transcription failed with status {attempt.StatusCode}: {attempt.ResponseBody}",
                    attempt.StatusCode,
                    attempt.ResponseBody);
            }

            var parsed = ParseTranscriptionResponse(attempt.ResponseBody, transcriptionModel);
            if (!string.IsNullOrWhiteSpace(parsed.Result.Text))
            {
                transcripts.Add(parsed.Result.Text.Trim());
            }

            foreach (var segment in parsed.Result.Segments)
            {
                var chunkSpeakerKey = $"{chunkIndex}:{segment.SpeakerLabel}";
                if (!speakerLabels.TryGetValue(chunkSpeakerKey, out var speakerLabel))
                {
                    speakerLabel = SpeakerLabel(speakerLabels.Count);
                    speakerLabels[chunkSpeakerKey] = speakerLabel;
                }

                segments.Add(segment with
                {
                    Id = $"chunk-{chunkIndex + 1}-{segment.Id}",
                    SpeakerLabel = speakerLabel,
                    StartMs = elapsedMs + segment.StartMs,
                    EndMs = elapsedMs + segment.EndMs
                });
            }

            elapsedMs += parsed.DurationMs;
            chunkIndex += 1;
        }

        return new MeetingAudioTranscriptionResult(
            string.Join("\n\n", transcripts).Trim(),
            segments.Count > 0 ? "openai_diarization" : "openai_plain",
            segments);
    }

    private static async Task<IReadOnlyCollection<AudioChunk>> SplitAudioWithFfmpegAsync(
        string fileName,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var extension = ResolveAudioExtension(fileName, mimeType);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"crm-ai-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var inputPath = Path.Combine(tempRoot, $"input{extension}");
            await File.WriteAllBytesAsync(inputPath, content, cancellationToken);
            var outputPattern = Path.Combine(tempRoot, $"chunk-%03d{extension}");
            var segmentSeconds = await ResolveSegmentSecondsAsync(inputPath, content.Length, cancellationToken);
            var arguments = $"-hide_banner -loglevel error -y -i {Quote(inputPath)} -map 0:a:0 -c copy -f segment -segment_time {segmentSeconds.ToString(CultureInfo.InvariantCulture)} -reset_timestamps 1 {Quote(outputPattern)}";
            var result = await RunProcessAsync("ffmpeg", arguments, cancellationToken);
            if (result.ExitCode != 0)
            {
                arguments = $"-hide_banner -loglevel error -y -i {Quote(inputPath)} -vn -acodec libopus -b:a 32k -f segment -segment_time {segmentSeconds.ToString(CultureInfo.InvariantCulture)} -reset_timestamps 1 {Quote(outputPattern)}";
                result = await RunProcessAsync("ffmpeg", arguments, cancellationToken);
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Nao foi possivel dividir o audio grande com ffmpeg: {result.Error}");
            }

            var chunkPaths = Directory.GetFiles(tempRoot, $"chunk-*{extension}")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (chunkPaths.Length == 0)
            {
                throw new InvalidOperationException("O ffmpeg nao gerou partes de audio para transcricao.");
            }

            var chunks = new List<AudioChunk>(chunkPaths.Length);
            for (var index = 0; index < chunkPaths.Length; index += 1)
            {
                var chunkContent = await File.ReadAllBytesAsync(chunkPaths[index], cancellationToken);
                if (chunkContent.Length > MaxOpenAiAudioUploadBytes)
                {
                    throw new InvalidOperationException("Uma parte do audio ainda ficou maior que o limite de upload da OpenAI. Reduza OPENAI_TRANSCRIPTION_SEGMENT_SECONDS.");
                }

                chunks.Add(new AudioChunk($"meet-audio-part-{index + 1:000}{extension}", NormalizeMimeType(mimeType), chunkContent));
            }

            return chunks;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Temporary audio cleanup is best effort.
            }
        }
    }

    private static async Task<int> ResolveSegmentSecondsAsync(string inputPath, int sizeBytes, CancellationToken cancellationToken)
    {
        var configuredValue = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_SEGMENT_SECONDS");
        var configuredSeconds = int.TryParse(configuredValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 30, DefaultSegmentSeconds)
            : DefaultSegmentSeconds;

        var probe = await RunProcessAsync("ffprobe", $"-v error -show_entries format=duration -of default=nw=1:nk=1 {Quote(inputPath)}", cancellationToken);
        if (probe.ExitCode != 0 || !double.TryParse(probe.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds) || durationSeconds <= 0)
        {
            return configuredSeconds;
        }

        var minimumParts = Math.Max(1, (int)Math.Ceiling(sizeBytes / (double)MaxOpenAiAudioUploadBytes));
        var secondsBySize = (int)Math.Floor(durationSeconds / minimumParts);
        return Math.Clamp(Math.Min(configuredSeconds, secondsBySize), 30, DefaultSegmentSeconds);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{fileName} nao esta disponivel no ambiente. Instale ffmpeg para processar gravacoes grandes.", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string ResolveAudioExtension(string fileName, string mimeType)
    {
        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }

        return NormalizeMimeType(mimeType) switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp3" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/wav" => ".wav",
            "audio/webm" => ".webm",
            _ => ".webm"
        };
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string BuildTranscriptionRequestJson(string transcriptionModel, string fileName, string mimeType, int sizeBytes, string? chunkingStrategy) =>
        JsonSerializer.Serialize(new
        {
            model = transcriptionModel,
            language = "pt",
            response_format = RequiresDiarization(transcriptionModel) ? "diarized_json" : "json",
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

    private static bool RequiresDiarization(string model) =>
        model.Contains("transcribe-diarize", StringComparison.OrdinalIgnoreCase);

    private static bool CanFallbackFromDiarization(string model, int statusCode) =>
        RequiresDiarization(model) && statusCode is not (401 or 403 or 429);

    private static ParsedTranscription ParseTranscriptionResponse(string responseBody, string model)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        var durationMs = root.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var durationSeconds)
            ? Math.Max(0, (int)Math.Round(durationSeconds * 1000d))
            : 0;
        if (!RequiresDiarization(model)
            || !root.TryGetProperty("segments", out var segmentItems)
            || segmentItems.ValueKind != JsonValueKind.Array)
        {
            return new ParsedTranscription(new MeetingAudioTranscriptionResult(text, "openai_plain", []), durationMs);
        }

        var segments = segmentItems.EnumerateArray()
            .Select((segment, index) => new MeetingAudioTranscriptionSegment(
                segment.TryGetProperty("id", out var id) ? id.GetString() ?? $"segment-{index + 1}" : $"segment-{index + 1}",
                segment.TryGetProperty("speaker", out var speaker) ? speaker.GetString() ?? "A" : "A",
                ReadMilliseconds(segment, "start"),
                ReadMilliseconds(segment, "end"),
                segment.TryGetProperty("text", out var segmentText) ? segmentText.GetString()?.Trim() ?? string.Empty : string.Empty))
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .ToArray();
        return new ParsedTranscription(
            new MeetingAudioTranscriptionResult(text, segments.Length > 0 ? "openai_diarization" : "openai_plain", segments),
            durationMs > 0 ? durationMs : segments.LastOrDefault()?.EndMs ?? 0);
    }

    private static int ReadMilliseconds(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var seconds)
            ? Math.Max(0, (int)Math.Round(seconds * 1000d))
            : 0;

    private static string SpeakerLabel(int index)
    {
        var value = index + 1;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value -= 1;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }
        return builder.ToString();
    }

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

    internal static bool IsLargeAudioFailure(string? responseBody) =>
        IsInputTooLarge(responseBody)
        || IsAudioDurationLimitFailure(responseBody)
        || (!string.IsNullOrWhiteSpace(responseBody)
            && (responseBody.Contains("25 MB", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("maximum content size", StringComparison.OrdinalIgnoreCase)
                || responseBody.Contains("file is too large", StringComparison.OrdinalIgnoreCase)));

    private static bool IsAudioDurationLimitFailure(string? responseBody) =>
        !string.IsNullOrWhiteSpace(responseBody)
        && responseBody.Contains("audio duration", StringComparison.OrdinalIgnoreCase)
        && responseBody.Contains("maximum", StringComparison.OrdinalIgnoreCase);

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
    private sealed record ParsedTranscription(MeetingAudioTranscriptionResult Result, int DurationMs);

    private sealed record AudioChunk(string FileName, string MimeType, byte[] Content);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
