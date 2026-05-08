namespace CrmAi.Domain;

public sealed record OpportunityEvent(
    string EventId,
    string Type,
    DateTime OccurredAt,
    string OpportunityId,
    string? UserId,
    IReadOnlyDictionary<string, object?> Data);

public sealed record OpportunityAnalysisContext(
    OpportunitySnapshot Opportunity,
    PipelineStageSnapshot Stage,
    IReadOnlyCollection<NoteSnapshot> Notes,
    IReadOnlyCollection<ActivitySnapshot> Activities,
    IReadOnlyCollection<ContactSnapshot> Contacts,
    IReadOnlyCollection<UserSnapshot> Users,
    IReadOnlyCollection<HistoryEventSnapshot> HistoryEvents,
    IReadOnlyCollection<CommercialAnalysisMetricRuleSnapshot> MetricRules,
    OpportunityEvent TriggerEvent);

public sealed record OpportunitySnapshot(
    string Id,
    string Name,
    string PipelineId,
    string StageId,
    string? AccountId,
    string? OwnerUserId,
    decimal Value,
    string Status,
    bool CurrentRiskFlag,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastActivityAt);

public sealed record PipelineStageSnapshot(string Id, string Title, int Position);

public sealed record NoteSnapshot(string Id, string Text, string? AuthorUserId, DateTime CreatedAt);

public sealed record ActivitySnapshot(
    string Id,
    string Title,
    string ActivityType,
    string Channel,
    string Status,
    DateTime DateAt,
    string? Notes,
    string? OwnerUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ContactSnapshot(
    string Id,
    string? AccountId,
    string Name,
    string Role,
    string Email,
    string? Phone,
    string? OwnerUserId,
    string Status);

public sealed record UserSnapshot(string Id, string Name, string Role, bool IsActive);

public sealed record HistoryEventSnapshot(string Id, string Event, string? UserId, DateTime CreatedAt);

public sealed record CommercialAnalysisMetricRuleSnapshot(
    string Id,
    string MetricKey,
    string? PipelineId,
    string? StageId,
    string Level,
    string Operator,
    decimal ThresholdValue,
    string ThresholdUnit);

public sealed record OpportunityAnalysisSnapshotUpdate(
    DateTime SnapshotAt,
    int DaysInStage,
    int ActivitiesOpen,
    int ActivitiesOverdue,
    int LastInteractionDays,
    DateTime? LastInteractionAt,
    int HealthScore,
    int ConfidenceScore);

public sealed record RiskAnalysisResult(
    RiskLevel RiskLevel,
    int RiskScore,
    IReadOnlyCollection<string> Reasons,
    IReadOnlyCollection<string> Recommendations,
    OpportunityAnalysisSnapshotUpdate SnapshotUpdate);

public enum RiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3
}
