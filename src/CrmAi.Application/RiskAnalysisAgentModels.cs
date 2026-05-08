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
    AnalysisOpportunitySummary Opportunity,
    AnalysisPipelineSummary Pipeline,
    AnalysisActivitySummary Activities,
    IReadOnlyCollection<AnalysisNoteSummary> RecentNotes,
    IReadOnlyCollection<AnalysisContactSummary> Contacts,
    IReadOnlyCollection<AnalysisUserSummary> Users,
    IReadOnlyCollection<AnalysisHistoryEventSummary> RecentHistoryEvents,
    AnalysisTriggerEventSummary TriggerEvent,
    CommercialRuleAssessment CommercialRuleAssessment);

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
    string? OwnerUserId);

public sealed record AnalysisNoteSummary(string Text, string? AuthorUserId, DateTime CreatedAt);

public sealed record AnalysisContactSummary(string Name, string Role, string Status, string? OwnerUserId);

public sealed record AnalysisUserSummary(string Name, string Role, bool IsActive);

public sealed record AnalysisHistoryEventSummary(string Event, string? UserId, DateTime CreatedAt);

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
