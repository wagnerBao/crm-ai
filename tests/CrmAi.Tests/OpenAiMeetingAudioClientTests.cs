using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CrmAi.Application;

namespace CrmAi.Tests;

public sealed class OpenAiMeetingAudioClientTests
{
    [Fact]
    public void AddApplication_ConfiguresExtendedTimeoutOnlyForMeetingAudioClient()
    {
        var services = new ServiceCollection();
        services.AddApplication(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var clientFactory = provider.GetRequiredService<IHttpClientFactory>();
        using var meetingAudioClient = clientFactory.CreateClient(nameof(IOpenAiMeetingAudioClient));
        using var riskAnalysisClient = clientFactory.CreateClient(nameof(IOpenAiRiskAnalysisClient));

        Assert.Equal(TimeSpan.FromMinutes(10), meetingAudioClient.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(100), riskAnalysisClient.Timeout);
    }

    [Fact]
    public async Task AnalyzeAsync_RequestsSuggestedActivityForMeetingAgent()
    {
        var response = """{"output":[{"content":[{"type":"output_text","text":"{\"summary\":\"Resumo\",\"objections\":[],\"objectionBreakOpportunities\":[],\"nextStep\":\"Agendar retorno\",\"shouldCreateActivity\":true,\"activityTitle\":\"Agendar retorno\",\"activityNotes\":\"Retomar a conversa conforme combinado.\",\"activityDueAt\":null,\"confidenceScore\":88,\"reasons\":[\"Retorno combinado durante a reunião\"]}"}]}]}""";
        var handler = new CapturingHandler((HttpStatusCode.OK, response));
        var client = CreateClient(handler);

        var result = await client.AnalyzeAsync(
            CreateSettings(),
            new MeetingAudioAnalysisInput("Transcrição", null, null, "Meet", null),
            AiAgentInvocationContext.Unknown,
            CancellationToken.None);

        Assert.True(result.ShouldCreateActivity);
        Assert.Equal("Agendar retorno", result.ActivityTitle);
        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.Contains("meeting_audio_analysis_result", requestBody);
        Assert.DoesNotContain("call_audio_analysis_result", requestBody);
        Assert.Contains("shouldCreateActivity", requestBody);
        Assert.Contains("suggestedTags", requestBody);
        Assert.Contains("suggestedContactFields", requestBody);
        Assert.Contains("exclusivamente dessa lista", requestBody);
        Assert.Contains("valor que ja estiver em currentValue", requestBody);
        Assert.Contains("Para reunioes e ligacoes", requestBody);
        Assert.Contains("Nao crie atividade para conversa social", requestBody);
    }

    [Fact]
    public async Task AnalyzeAsync_KeepsSuggestedActivityForWhatsappCallAgent()
    {
        var response = """{"output":[{"content":[{"type":"output_text","text":"{\"summary\":\"Resumo\",\"objections\":[],\"objectionBreakOpportunities\":[],\"nextStep\":\"Enviar material\",\"shouldCreateActivity\":true,\"activityTitle\":\"Enviar material\",\"activityNotes\":\"Enviar os arquivos combinados.\",\"activityDueAt\":null,\"confidenceScore\":90,\"reasons\":[\"Ação combinada\"]}"}]}]}""";
        var handler = new CapturingHandler((HttpStatusCode.OK, response));
        var client = CreateClient(handler);
        var settings = CreateSettings() with { AgentKey = "call-audio-analysis" };

        var result = await client.AnalyzeAsync(
            settings,
            new MeetingAudioAnalysisInput("Transcrição", null, null, "Ligação", null),
            AiAgentInvocationContext.Unknown,
            CancellationToken.None);

        Assert.True(result.ShouldCreateActivity);
        Assert.Equal("Enviar material", result.ActivityTitle);
        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.Contains("call_audio_analysis_result", requestBody);
        Assert.Contains("shouldCreateActivity", requestBody);
        Assert.Contains("Nao crie atividade para conversa social", requestBody);
    }

    [Fact]
    public async Task TranscribeAsync_RequestsDiarizedJsonAndAutomaticChunking()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", null);

        try
        {
            var handler = new CapturingHandler((HttpStatusCode.OK, """{"text":"Transcricao ok","duration":2.5,"segments":[{"id":"seg-1","start":0.2,"end":2.4,"text":"Transcricao ok","speaker":"A"}]}"""));
            var client = CreateClient(handler);

            var transcript = await client.TranscribeAsync(
                CreateSettings(),
                "meet-audio.webm",
                "audio/webm;codecs=opus",
                [1, 2, 3],
                AiAgentInvocationContext.Unknown,
                CancellationToken.None);

            Assert.Equal("Transcricao ok", transcript.Text);
            var segment = Assert.Single(transcript.Segments);
            Assert.Equal("A", segment.SpeakerLabel);
            Assert.Equal(200, segment.StartMs);
            Assert.Equal(2400, segment.EndMs);
            Assert.Equal("openai_diarization", transcript.Source);
            var requestBody = Assert.Single(handler.RequestBodies);
            Assert.Contains("name=chunking_strategy", requestBody);
            Assert.Contains("name=response_format", requestBody);
            Assert.Contains("diarized_json", requestBody);
            Assert.Contains("name=model", requestBody);
            Assert.Contains("gpt-4o-transcribe-diarize", requestBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", previousModel);
        }
    }

    [Fact]
    public async Task TranscribeAsync_RetriesWithChunking_WhenOpenAiReportsInputTooLarge()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", "gpt-4o-transcribe");

        try
        {
            var handler = new CapturingHandler(
                (HttpStatusCode.BadRequest, """{"error":{"code":"input_too_large","message":"Total number of tokens in instructions + audio is too large for this model"}}"""),
                (HttpStatusCode.OK, """{"text":"Transcricao ok"}"""));
            var client = CreateClient(handler);

            var transcript = await client.TranscribeAsync(
                CreateSettings(),
                "meet-audio.webm",
                "audio/webm;codecs=opus",
                [1, 2, 3],
                AiAgentInvocationContext.Unknown,
                CancellationToken.None);

            Assert.Equal("Transcricao ok", transcript.Text);
            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.DoesNotContain("name=chunking_strategy", handler.RequestBodies[0]);
            Assert.Contains("name=chunking_strategy", handler.RequestBodies[1]);
            Assert.Contains("auto", handler.RequestBodies[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", previousModel);
        }
    }

    [Fact]
    public async Task TranscribeAsync_FallsBackToPlainTranscription_WhenDiarizationIsUnavailable()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        var previousFallbackModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", null);
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL", null);

        try
        {
            var handler = new CapturingHandler(
                (HttpStatusCode.NotFound, """{"error":{"message":"model unavailable"}}"""),
                (HttpStatusCode.OK, """{"text":"Transcricao sem diarizacao"}"""));
            var transcript = await CreateClient(handler).TranscribeAsync(
                CreateSettings(), "meet-audio.webm", "audio/webm", [1, 2, 3],
                AiAgentInvocationContext.Unknown, CancellationToken.None);

            Assert.Equal("openai_plain", transcript.Source);
            Assert.Empty(transcript.Segments);
            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.Contains("gpt-4o-transcribe-diarize", handler.RequestBodies[0]);
            Assert.Contains("gpt-4o-transcribe", handler.RequestBodies[1]);
            Assert.DoesNotContain("diarized_json", handler.RequestBodies[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", previousModel);
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL", previousFallbackModel);
        }
    }

    [Fact]
    public async Task TranscribeAsync_FallsBackToWhisper_WhenChunkingStillExceedsInputLimit()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        var previousFallbackModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", "gpt-4o-transcribe");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL", "whisper-1");

        try
        {
            var tooLarge = """{"error":{"code":"input_too_large","message":"Total number of tokens in instructions + audio is too large for this model"}}""";
            var handler = new CapturingHandler(
                (HttpStatusCode.BadRequest, tooLarge),
                (HttpStatusCode.BadRequest, tooLarge),
                (HttpStatusCode.OK, """{"text":"Transcricao via fallback"}"""));
            var client = CreateClient(handler);

            var transcript = await client.TranscribeAsync(
                CreateSettings(),
                "meet-audio.webm",
                "audio/webm;codecs=opus",
                [1, 2, 3],
                AiAgentInvocationContext.Unknown,
                CancellationToken.None);

            Assert.Equal("Transcricao via fallback", transcript.Text);
            Assert.Equal(3, handler.RequestBodies.Count);
            Assert.Contains("gpt-4o-transcribe", handler.RequestBodies[0]);
            Assert.Contains("name=chunking_strategy", handler.RequestBodies[1]);
            Assert.Contains("gpt-4o-transcribe", handler.RequestBodies[1]);
            Assert.Contains("whisper-1", handler.RequestBodies[2]);
            Assert.DoesNotContain("name=chunking_strategy", handler.RequestBodies[2]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", previousModel);
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL", previousFallbackModel);
        }
    }

    [Fact]
    public void IsLargeAudioFailure_RecognizesOpenAiDurationLimitResponse()
    {
        const string response = """{"error":{"code":"invalid_value","message":"audio duration 1476.06 seconds is longer than 1400 seconds which is the maximum for this model"}}""";

        Assert.True(OpenAiMeetingAudioClient.IsLargeAudioFailure(response));
    }

    [Fact]
    public void IsLargeAudioFailure_DoesNotClassifyGenericInvalidValueAsLargeAudio()
    {
        const string response = """{"error":{"code":"invalid_value","message":"audio format is not supported"}}""";

        Assert.False(OpenAiMeetingAudioClient.IsLargeAudioFailure(response));
    }

    private static OpenAiMeetingAudioClient CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new OpenAiRiskAnalysisOptions()),
            new NullInvocationLogStore());

    private static AiAgentRuntimeSettings CreateSettings() =>
        new(
            "meeting-service-analysis",
            true,
            "openai",
            "gpt-4.1-mini",
            "test-api-key",
            "Analise reunioes.",
            1,
            null,
            []);

    private sealed class CapturingHandler(params (HttpStatusCode StatusCode, string Body)[] responses) : HttpMessageHandler
    {
        private int index;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var response = responses[Math.Min(index, responses.Length - 1)];
            index += 1;
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body)
            };
        }
    }

    private sealed class NullInvocationLogStore : IAiAgentInvocationLogStore
    {
        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
