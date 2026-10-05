namespace CrmAi.Domain;

// Derived once by the meeting analysis; source binding is added by the application.
public sealed record MeetingRiskContext(
    IReadOnlyCollection<MeetingRiskFact> Facts,
    int ConfidenceScore,
    bool NeedsFullTranscript);

public sealed record MeetingRiskFact(
    string Kind,
    string Description,
    string? Participant,
    string? DueAt,
    string EvidenceExcerpt);

public sealed record StoredMeetingRiskContext(
    string Version,
    string TranscriptFingerprint,
    MeetingRiskContext Context);
