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
    IReadOnlyCollection<string>? AgentInsights = null);

public sealed record MeetingAudioAnalysisResult(
    string Summary,
    IReadOnlyCollection<string> Objections,
    IReadOnlyCollection<string> ObjectionBreakOpportunities,
    string NextStep);

public sealed record OpenAiMeetingAudioAnalysisResponse(
    string Summary,
    IReadOnlyCollection<string> Objections,
    IReadOnlyCollection<string> ObjectionBreakOpportunities,
    string NextStep);

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
    string? CompanyId);
