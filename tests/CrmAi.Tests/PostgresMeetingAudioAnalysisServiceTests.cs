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
    public void ShouldCreateActivitySuggestion_RequiresSupportedSourceContactAndCompleteAction()
    {
        var recording = CreateRecording("whatsapp_call", Guid.NewGuid().ToString());
        var analysis = new OpenAiMeetingAudioAnalysisResponse(
            "Resumo", [], [], "Enviar material ao cliente.", true,
            "Enviar material solicitado", "Enviar os prints e áudios combinados.", null, 92, ["Ação combinada durante a ligação."]);

        Assert.True(PostgresMeetingAudioAnalysisService.ShouldCreateActivitySuggestion(recording, analysis));
        Assert.True(PostgresMeetingAudioAnalysisService.ShouldCreateActivitySuggestion(CreateRecording("google_meet", recording.ContactId), analysis));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateActivitySuggestion(CreateRecording("manual_upload", recording.ContactId), analysis));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateActivitySuggestion(CreateRecording("whatsapp_call", null), analysis));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateActivitySuggestion(recording, analysis with { ShouldCreateActivity = false }));
    }

    [Fact]
    public void ActivitySuggestionPersistence_IsIdempotentAndUsesTheSourceAgentAndChannel()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("@agentKey, 'activity', 'pending'", source, StringComparison.Ordinal);
        Assert.Contains("ResolveAgentKey(recording.SourceKind)", source, StringComparison.Ordinal);
        Assert.Contains("? \"call\"", source, StringComparison.Ordinal);
        Assert.Contains(": \"meeting\"", source, StringComparison.Ordinal);
        Assert.Contains("on conflict (run_id, suggestion_type)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldCreateNoteSuggestion_RequiresSupportedSourceContactSummaryAndMinimumConfidence()
    {
        var recording = CreateRecording("google_meet", Guid.NewGuid().ToString());
        var analysis = new OpenAiMeetingAudioAnalysisResponse(
            "Resumo", [], [], "Enviar material.", ConfidenceScore: 80, Reasons: ["Resumo sustentado pela conversa."]);

        Assert.True(PostgresMeetingAudioAnalysisService.ShouldCreateNoteSuggestion(recording, analysis, "Resumo estruturado"));
        Assert.True(PostgresMeetingAudioAnalysisService.ShouldCreateNoteSuggestion(CreateRecording("whatsapp_call", recording.ContactId), analysis, "Resumo estruturado"));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateNoteSuggestion(CreateRecording("manual_upload", recording.ContactId), analysis, "Resumo estruturado"));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateNoteSuggestion(CreateRecording("google_meet", null), analysis, "Resumo estruturado"));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateNoteSuggestion(recording, analysis with { ConfidenceScore = 59 }, "Resumo estruturado"));
        Assert.False(PostgresMeetingAudioAnalysisService.ShouldCreateNoteSuggestion(recording, analysis, " "));
    }

    [Fact]
    public void NoteSuggestionPersistence_IsIdempotentVersionedAndCarriesAnExplicitTarget()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("@agentKey, 'note', 'pending'", source, StringComparison.Ordinal);
        Assert.Contains("targetType", source, StringComparison.Ordinal);
        Assert.Contains("targetId", source, StringComparison.Ordinal);
        Assert.Contains("prompt_fingerprint", source, StringComparison.Ordinal);
        Assert.Contains("on conflict (run_id, suggestion_type)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TagSuggestions_AcceptOnlyExistingIdsWithLiteralTranscriptEvidence()
    {
        var priceTag = new MeetingTagOptionInput(Guid.NewGuid().ToString(), "Objeção de preço", null);
        var competitorTag = new MeetingTagOptionInput(Guid.NewGuid().ToString(), "Concorrente", null);
        var unknownId = Guid.NewGuid().ToString();
        var suggestions = new[]
        {
            new OpenAiConversationTagSuggestion(priceTag.Id, "Cliente questionou o valor.", "o preço ficou acima do orçamento"),
            new OpenAiConversationTagSuggestion(competitorTag.Id, "Concorrente mencionado.", "empresa que não foi mencionada"),
            new OpenAiConversationTagSuggestion(unknownId, "Tag inventada.", "preço ficou acima"),
            new OpenAiConversationTagSuggestion(priceTag.Id, "Duplicada.", "preço ficou acima")
        };

        var result = PostgresMeetingAudioAnalysisService.ValidateTagSuggestions(
            [priceTag, competitorTag], suggestions, "Cliente: o preço ficou acima do orçamento disponível.");

        var selected = Assert.Single(result);
        Assert.Equal(priceTag.Id, selected.TagId);
    }

    [Fact]
    public void TagSuggestionPersistence_RevalidatesTenantStatusAndExistingContactLinks()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("tag.company_id = @companyId", source, StringComparison.Ordinal);
        Assert.Contains("tag.status = 'active'", source, StringComparison.Ordinal);
        Assert.Contains("current.entity_type = 'contact'", source, StringComparison.Ordinal);
        Assert.Contains("@agentKey, 'tags', 'pending'", source, StringComparison.Ordinal);
        Assert.Contains("on conflict (run_id, suggestion_type)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ContactFieldSuggestions_RequireExistingTypedFieldsAndLiteralEvidence()
    {
        var segment = new MeetingContactFieldOptionInput(
            Guid.NewGuid().ToString(), "Segmento", "text", ["Enterprise", "SMB"], "SMB");
        var employees = new MeetingContactFieldOptionInput(
            Guid.NewGuid().ToString(), "Funcionarios", "number", [], null);
        var result = PostgresMeetingAudioAnalysisService.ValidateContactFieldSuggestions(
            [segment, employees],
            [
                new OpenAiConversationContactFieldSuggestion(segment.Id, "enterprise", "Porte informado.", "somos uma empresa Enterprise"),
                new OpenAiConversationContactFieldSuggestion(employees.Id, "120", "Quantidade informada.", "temos 120 funcionários"),
                new OpenAiConversationContactFieldSuggestion(Guid.NewGuid().ToString(), "inventado", "Campo desconhecido.", "temos 120 funcionários"),
                new OpenAiConversationContactFieldSuggestion(employees.Id, "130", "Duplicada.", "temos 120 funcionários")
            ],
            "Cliente: somos uma empresa Enterprise e temos 120 funcionários.");

        Assert.Collection(result,
            item =>
            {
                Assert.Equal(segment.Id, item.FieldId);
                Assert.Equal("Enterprise", item.Value);
            },
            item =>
            {
                Assert.Equal(employees.Id, item.FieldId);
                Assert.Equal("120", item.Value);
            });
    }

    [Fact]
    public void ContactFieldSuggestionPersistence_RevalidatesTenantDefinitionAndPreviousValue()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("definition.company_id = @companyId", source, StringComparison.Ordinal);
        Assert.Contains("definition.entity_type = 'contact'", source, StringComparison.Ordinal);
        Assert.Contains("definition.is_active = true", source, StringComparison.Ordinal);
        Assert.Contains("original.CurrentValue, currentValue", source, StringComparison.Ordinal);
        Assert.Contains("@agentKey, 'contact_fields', 'pending'", source, StringComparison.Ordinal);
        Assert.Contains("on conflict (run_id, suggestion_type)", source, StringComparison.Ordinal);
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
    public void DiarizedTranscription_PersistsSegmentsAndOnlyRetranscribesWhenExplicitlyForced()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresMeetingAudioAnalysisService.cs");

        Assert.Contains("forceRetranscription", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(transcript) || forceRetranscription", source, StringComparison.Ordinal);
        Assert.Contains("meeting_audio_transcription_versions", source, StringComparison.Ordinal);
        Assert.Contains("meeting_audio_transcription_speakers", source, StringComparison.Ordinal);
        Assert.Contains("meeting_audio_transcription_segments", source, StringComparison.Ordinal);
        Assert.Contains("Voz {segment.SpeakerLabel} · Desconhecido", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(reader.GetString(0), \"google_meet\"", source, StringComparison.Ordinal);
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
        Assert.Contains("update conversation_scorecards", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recording_id = @recordingId", source, StringComparison.Ordinal);
        Assert.Contains("and is_current", source, StringComparison.Ordinal);
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
