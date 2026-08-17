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
