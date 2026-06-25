using System.Net;
using Microsoft.Extensions.Options;
using CrmAi.Application;

namespace CrmAi.Tests;

public sealed class OpenAiMeetingAudioClientTests
{
    [Fact]
    public async Task TranscribeAsync_EnablesServerChunking_ForGptTranscriptionModels()
    {
        var previousModel = Environment.GetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL");
        Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", "gpt-4o-mini-transcribe");

        try
        {
            var handler = new CapturingHandler("""{"text":"Transcricao ok"}""");
            var client = new OpenAiMeetingAudioClient(
                new HttpClient(handler),
                Options.Create(new OpenAiRiskAnalysisOptions()),
                new NullInvocationLogStore());

            var transcript = await client.TranscribeAsync(
                CreateSettings(),
                "meet-audio.webm",
                "audio/webm;codecs=opus",
                [1, 2, 3],
                AiAgentInvocationContext.Unknown,
                CancellationToken.None);

            Assert.Equal("Transcricao ok", transcript);
            Assert.Contains("name=chunking_strategy", handler.RequestBody);
            Assert.Contains("auto", handler.RequestBody);
            Assert.Contains("name=model", handler.RequestBody);
            Assert.Contains("gpt-4o-mini-transcribe", handler.RequestBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_TRANSCRIPTION_MODEL", previousModel);
        }
    }

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

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }

    private sealed class NullInvocationLogStore : IAiAgentInvocationLogStore
    {
        public Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
