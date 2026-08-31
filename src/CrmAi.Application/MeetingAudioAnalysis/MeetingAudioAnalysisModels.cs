namespace CrmAi.Application;

public sealed record MeetingAudioAnalysisInput(
    string Transcript,
    string? OpportunityName,
    string? AccountName,
    string? ActivityTitle,
    string? ActivityNotes,
    IReadOnlyCollection<string>? Notes = null,
    IReadOnlyCollection<string>? Contacts = null,
    IReadOnlyCollection<string>? Activities = null,
    IReadOnlyCollection<string>? AgentInsights = null,
    MeetingScorecardTemplateInput? ScorecardTemplate = null,
    IReadOnlyCollection<MeetingTagOptionInput>? AvailableTags = null,
    IReadOnlyCollection<MeetingContactFieldOptionInput>? AvailableContactFields = null);

public sealed record MeetingTagOptionInput(string Id, string Name, string? Description);
public sealed record MeetingContactFieldOptionInput(
    string Id,
    string Label,
    string FieldType,
    IReadOnlyCollection<string> Options,
    string? CurrentValue);

public sealed record MeetingScorecardTemplateInput(string Id, string Name, int Version, IReadOnlyCollection<MeetingScorecardCriterionInput> Criteria);
public sealed record MeetingScorecardCriterionInput(string Key, string Title, string? Description, decimal Weight, string EvaluationInstruction, IReadOnlyCollection<string> PositiveExamples, IReadOnlyCollection<string> NegativeExamples, int ScoreMin, int ScoreMax, bool IsRequired);

public sealed record MeetingAudioAnalysisResult(
    string Summary,
    IReadOnlyCollection<string> Objections,
    IReadOnlyCollection<string> ObjectionBreakOpportunities,
    string NextStep);

public sealed record OpenAiMeetingAudioAnalysisResponse(
    string Summary,
    IReadOnlyCollection<string> Objections,
    IReadOnlyCollection<string> ObjectionBreakOpportunities,
    string NextStep,
    bool ShouldCreateActivity = false,
    string? ActivityTitle = null,
    string? ActivityNotes = null,
    string? ActivityDueAt = null,
    int ConfidenceScore = 0,
    IReadOnlyCollection<string>? Reasons = null,
    IReadOnlyCollection<OpenAiConversationScorecardItem>? ScorecardItems = null,
    IReadOnlyCollection<OpenAiConversationTagSuggestion>? SuggestedTags = null,
    IReadOnlyCollection<OpenAiConversationContactFieldSuggestion>? SuggestedContactFields = null);

public sealed record OpenAiConversationTagSuggestion(string TagId, string Reason, string EvidenceExcerpt);
public sealed record OpenAiConversationContactFieldSuggestion(string FieldId, string Value, string Reason, string EvidenceExcerpt);

public sealed record OpenAiConversationScorecardItem(string CriterionKey, int Score, int ConfidenceScore, string Justification, string? Recommendation, IReadOnlyCollection<OpenAiConversationEvidence> Evidence);
public sealed record OpenAiConversationEvidence(string Excerpt, string? Participant, long? StartMs, long? EndMs, string Source, int ConfidenceScore);

public sealed record MeetingAudioTranscriptionSegment(
    string Id,
    string SpeakerLabel,
    int StartMs,
    int EndMs,
    string Text);

public sealed record MeetingAudioTranscriptionResult(
    string Text,
    string Source,
    IReadOnlyCollection<MeetingAudioTranscriptionSegment> Segments);

public sealed record MeetingAudioRecordingPayload(
    string Id,
    string MeetingId,
    string? ActivityId,
    string? OpportunityId,
    string? AccountId,
    string FileName,
    string MimeType,
    byte[] Content,
    string? OpportunityName,
    string? AccountName,
    string? ActivityTitle,
    string? ActivityNotes,
    string? CompanyId,
    string SourceKind = "google_meet",
    string? ContactId = null,
    string? OwnerUserId = null,
    string? Transcript = null,
    string? PipelineId = null,
    string? StageId = null,
    string? GroupId = null,
    string? ActivityType = null,
    string? TranscriptionVersionId = null);
