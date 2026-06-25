using System.Net;
using Microsoft.Extensions.Options;
using CrmAi.Application;

namespace CrmAi.Tests;

public sealed class OpenAiMeetingAudioClientTests
{
    [Fact]
    public async Task TranscribeAsync_SendsSingleRequestWithoutChunking_WhenAudioFits()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", null);

        try
        {
            var handler = new CapturingHandler((HttpStatusCode.OK, """{"text":"Transcricao ok"}"""));
            var client = CreateClient(handler);

            var transcript = await client.TranscribeAsync(
                CreateSettings(),
                "meet-audio.webm",
                "audio/webm;codecs=opus",
                [1, 2, 3],
                AiAgentInvocationContext.Unknown,
                CancellationToken.None);

            Assert.Equal("Transcricao ok", transcript);
            var requestBody = Assert.Single(handler.RequestBodies);
            Assert.DoesNotContain("name=chunking_strategy", requestBody);
            Assert.Contains("name=model", requestBody);
            Assert.Contains("gpt-4o-transcribe", requestBody);
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

            Assert.Equal("Transcricao ok", transcript);
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
    public async Task TranscribeAsync_FallsBackToWhisper_WhenChunkingStillExceedsInputLimit()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        var previousFallbackModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", "gpt-4o-transcribe");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_FALLBACK_MODEL", null);

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

            Assert.Equal("Transcricao via fallback", transcript);
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
