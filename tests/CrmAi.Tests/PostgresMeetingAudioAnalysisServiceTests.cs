using CrmAi.Application;
using CrmAi.Infrastructure.Persistence;

namespace CrmAi.Tests;

public sealed class PostgresMeetingAudioAnalysisServiceTests
{
    [Theory]
    [InlineData("google_meet", "meeting-service-analysis")]
    [InlineData(null, "meeting-service-analysis")]
    [InlineData("whatsapp_call", "call-audio-analysis")]
    [InlineData("WHATSAPP_CALL", "call-audio-analysis")]
    public void ResolveAgentKey_SelectsSettingsWithoutChangingMeetDefault(string? sourceKind, string expected)
    {
        Assert.Equal(expected, PostgresMeetingAudioAnalysisService.ResolveAgentKey(sourceKind));
    }

    [Fact]
    public void ShouldCreateCallSuggestion_RequiresWhatsappCallContactAndCompleteAction()
    {
        var recording = CreateRecording("whatsapp_call", Guid.NewGuid().ToString());
        var analysis = new OpenAiMeetingAudioAnalysisResponse(
            "Resumo", [], [], "Enviar material ao cliente.", true,
            "Enviar material solicitado", "Enviar os prints e áudios combinados.", null, 92, ["Ação combinada durante a ligação."]);

        Assert.True(PostgresMeetingAudioAnalysisService.ShouldCreateCallSuggestion(recording, analysis));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateCallSuggestion(CreateRecording("google_meet", recording.ContactId), analysis));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateCallSuggestion(CreateRecording("whatsapp_call", null), analysis));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateCallSuggestion(recording, analysis with { ShouldCreateActivity = false }));
    }

    [Fact]
    public void CallSuggestionPersistence_IsIdempotentByRecordingAndDoesNotRunForMeet()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("'call-audio-analysis', 'activity', 'pending'", source, StringComparison.Ordinal);
        Assert.Contains("on conflict (run_id, suggestion_type)", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(recording.SourceKind, \"whatsapp_call\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredAnalysisPersistence_DualWritesAndPreservesVersionHistory()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("update meeting_audio_recordings", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update conversation_analysis_results", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("analysis_status = 'completed'", source, StringComparison.Ordinal);
        Assert.Contains("set is_current = false", source, StringComparison.Ordinal);
        Assert.Contains("ux_conversation_analysis_active", source, StringComparison.Ordinal);
        Assert.Contains("recording.Transcript", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptFingerprint_CoversSystemAndContextInstructions()
    {
        var first = new AiAgentRuntimeSettings("meeting-service-analysis", true, "openai", "model", null, "system", 1, "context", []);
        var same = first with { };
        var changedContext = first with { ContextInstructions = "other context" };

        Assert.Equal(PostgresMeetingAudioAnalysisService.PromptFingerprint(first), PostgresMeetingAudioAnalysisService.PromptFingerprint(same));
        Assert.NotEqual(PostgresMeetingAudioAnalysisService.PromptFingerprint(first), PostgresMeetingAudioAnalysisService.PromptFingerprint(changedContext));
    }

    [Fact]
    public void ScorecardNormalization_RequiresLiteralEvidenceAndDoesNotInventTimestamps()
    {
        var coveredCriterion = Criterion("discovery", "Descoberta");
        var uncoveredCriterion = Criterion("objections", "Objeções");
        var template = new PostgresMeetingAudioAnalysisService.ScorecardTemplate(
            Guid.NewGuid(), Guid.NewGuid(), 1, "Padrao", [coveredCriterion, uncoveredCriterion]);
        var modelItems = new[]
        {
            new OpenAiConversationScorecardItem("discovery", 82, 90, "Investigou a necessidade.", "Aprofundar impacto.",
                [new OpenAiConversationEvidence("precisamos reduzir o prazo", "Cliente", 1000, 2000, "transcript", 95)]),
            new OpenAiConversationScorecardItem("objections", 70, 80, "Tratou a objeção.", null,
                [new OpenAiConversationEvidence("trecho que nao existe", null, null, null, "transcript", 80)])
        };

        var result = PostgresMeetingAudioAnalysisService.NormalizeScorecardItems(
            template, modelItems, "Cliente: precisamos   reduzir o prazo para concluir a entrega.").ToArray();

        Assert.True(result[0].IsCovered);
        Assert.Equal(82, result[0].Score);
        Assert.Null(result[0].Evidence.Single().StartMs);
        Assert.Null(result[0].Evidence.Single().EndMs);
        Assert.False(result[1].IsCovered);
        Assert.Equal(0, result[1].Confidence);
        Assert.Empty(result[1].Evidence);
    }

    [Fact]
    public void ScorecardPersistence_UsesAnalysisVersionAndExcludesUncoveredWeight()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("conversation_scorecards", source, StringComparison.Ordinal);
        Assert.Contains("conversation_scorecard_items", source, StringComparison.Ordinal);
        Assert.Contains("items.Where(item => item.IsCovered)", source, StringComparison.Ordinal);
        Assert.Contains("template_version", source, StringComparison.Ordinal);
    }

    private static PostgresMeetingAudioAnalysisService.ScorecardCriterion Criterion(string key, string title) =>
        new(Guid.NewGuid(), key, title, null, 10m, "Avalie.", [], [], 0, 100, true);

    private static MeetingAudioRecordingPayload CreateRecording(string sourceKind, string? contactId) =>
        new(
            Guid.NewGuid().ToString(),
            "meeting-id",
            Guid.NewGuid().ToString(),
            null,
            null,
            "audio.webm",
            "audio/webm",
            [1],
            null,
            null,
            "Ligação",
            null,
            Guid.NewGuid().ToString(),
            sourceKind,
            contactId,
            Guid.NewGuid().ToString());

    private static string ReadSource(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }

        throw new FileNotFoundException($"Source file not found: {relativePath}");
    }
}
