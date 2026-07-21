using CrmAi.Domain;

namespace CrmAi.Application;

public sealed record RiskAnalysisAgentRequest(
    RiskAnalysisAgentInput Input,
    OpportunityAnalysisSnapshotUpdate SnapshotUpdate);

public sealed record OpenAiRiskAnalysisResponse(
    string RiskLevel,
    int RiskScore,
    IReadOnlyCollection<string> Reasons,
    IReadOnlyCollection<string> Recommendations);

public sealed record RiskAnalysisAgentInput(
    AnalysisOpportunitySummary? Opportunity,
    AnalysisAccountSummary? Account,
    IReadOnlyCollection<AnalysisProductSummary> Products,
    AnalysisPipelineSummary Pipeline,
    AnalysisActivitySummary Activities,
    IReadOnlyCollection<AnalysisNoteSummary> RecentNotes,
    IReadOnlyCollection<AnalysisContactSummary> Contacts,
    IReadOnlyCollection<AnalysisUserSummary> Users,
    IReadOnlyCollection<AnalysisHistoryEventSummary> RecentHistoryEvents,
    IReadOnlyCollection<AnalysisAgentInsightSummary> RelatedAgentInsights,
    AnalysisTriggerEventSummary TriggerEvent,
    CommercialRuleAssessment CommercialRuleAssessment,
    IReadOnlyCollection<AnalysisMeetingAudioSummary>? MeetingAudioAnalyses = null);

public sealed record AnalysisOpportunitySummary(
    string Id,
    string Name,
    string Status,
    decimal Value,
    bool CurrentRiskFlag,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastActivityAt,
    DateTime? LatestInteractionAt,
    int LastInteractionDays,
    int InteractionCount,
    DateTime AnalyzedAt);

public sealed record AnalysisPipelineSummary(
    string PipelineId,
    string StageId,
    string StageTitle,
    int StagePosition,
    DateTime? StageChangedAt,
    int DaysInStage,
    bool HasStageRegression);

public sealed record AnalysisActivitySummary(
    int OpenCount,
    int OverdueCount,
    IReadOnlyCollection<AnalysisActivityItem> RecentItems);

public sealed record AnalysisActivityItem(
    string Title,
    string ActivityType,
    string Channel,
    string Status,
    DateTime DateAt,
    string? Notes,
    string? CompletedNotes,
    string? OwnerUserId);

public sealed record AnalysisMeetingAudioSummary(
    string? ActivityId,
    string Transcript,
    string Summary,
    DateTime? TranscribedAt,
    DateTime UpdatedAt);

public sealed record AnalysisNoteSummary(string Text, string? AuthorUserId, DateTime CreatedAt);

public sealed record AnalysisContactSummary(string Name, string Role, string Status, string? OwnerUserId);

public sealed record AnalysisUserSummary(string Name, string Role, bool IsActive);

public sealed record AnalysisHistoryEventSummary(string Event, string? UserId, DateTime CreatedAt);

public sealed record AnalysisAccountSummary(string Id, string Name, string Segment, string City, string Uf, string Status);

public sealed record AnalysisProductSummary(string Id, string Name, string Type, decimal Price, bool Featured, string Status, string Summary);

public sealed record AnalysisAgentInsightSummary(string Title, string Message, string Kind, decimal? Confidence, string Status, DateTime CreatedAt);

public sealed record AnalysisTriggerEventSummary(string Type, DateTime OccurredAt, string? UserId);

public sealed record CommercialRuleAssessment(
    IReadOnlyCollection<CommercialMetricSummary> Metrics,
    int HealthScore,
    int ConfidenceScore);

public sealed record CommercialMetricSummary(
    string MetricKey,
    decimal Value,
    IReadOnlyCollection<RuleSummary> ApplicableRules,
    RuleSummary? AppliedRule,
    int Penalty);

public sealed record RuleSummary(
    string Id,
    string MetricKey,
    string? PipelineId,
    string? StageId,
    string Level,
    string Operator,
    decimal ThresholdValue,
    string ThresholdUnit,
    bool Matched)
{
    public static RuleSummary FromRule(CommercialAnalysisMetricRuleSnapshot rule, bool matched)
        => new(
            rule.Id,
            rule.MetricKey,
            rule.PipelineId,
            rule.StageId,
            rule.Level,
            rule.Operator,
            rule.ThresholdValue,
            rule.ThresholdUnit,
            matched);
}
