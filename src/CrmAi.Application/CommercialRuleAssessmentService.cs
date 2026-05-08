using CrmAi.Domain;

namespace CrmAi.Application;

public sealed class CommercialRuleAssessmentService
{
    private const string StageTimeMetric = "stage_time";
    private const string OverdueActivitiesMetric = "overdue_activities";
    private const string OpportunityInteractionsMetric = "opportunity_interactions";
    private const string DaysWithoutInteractionMetric = "days_without_interaction";

    public CommercialRuleAssessment Calculate(
        OpportunityAnalysisContext context,
        int daysInStage,
        int openActivities,
        int overdueActivities,
        int interactionCount,
        int daysWithoutInteraction)
    {
        var metrics = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [StageTimeMetric] = daysInStage,
            [OverdueActivitiesMetric] = overdueActivities,
            [OpportunityInteractionsMetric] = interactionCount,
            [DaysWithoutInteractionMetric] = daysWithoutInteraction
        };

        var metricSummaries = metrics
            .Select(metric => BuildMetricSummary(context.MetricRules, context.Opportunity, metric.Key, metric.Value))
            .ToArray();

        var penalty = metricSummaries.Sum(assessment => assessment.Penalty);
        return new CommercialRuleAssessment(
            metricSummaries,
            Math.Max(0, 100 - penalty),
            CalculateConfidenceScore(context.MetricRules.Count, metricSummaries.Count(metric => metric.AppliedRule is not null), openActivities, context.Notes.Count));
    }

    private static CommercialMetricSummary BuildMetricSummary(
        IEnumerable<CommercialAnalysisMetricRuleSnapshot> rules,
        OpportunitySnapshot opportunity,
        string metricKey,
        decimal value)
    {
        var applicableRules = rules
            .Where(rule => string.Equals(rule.MetricKey, metricKey, StringComparison.OrdinalIgnoreCase))
            .Where(rule => rule.PipelineId is null || string.Equals(rule.PipelineId, opportunity.PipelineId, StringComparison.OrdinalIgnoreCase))
            .Where(rule => rule.StageId is null || string.Equals(rule.StageId, opportunity.StageId, StringComparison.OrdinalIgnoreCase))
            .Select(rule => new RuleMatchCandidate(rule, CalculateScopeScore(rule), MatchesRule(rule, value), ToPenalty(rule.Level)))
            .ToArray();

        var scopedRules = applicableRules
            .GroupBy(candidate => candidate.ScopeScore)
            .OrderByDescending(group => group.Key)
            .FirstOrDefault();

        var matchingRule = scopedRules
            ?.Where(candidate => candidate.Matches)
            .OrderByDescending(candidate => candidate.Penalty)
            .FirstOrDefault();

        return new CommercialMetricSummary(
            metricKey,
            value,
            scopedRules?.Select(candidate => RuleSummary.FromRule(candidate.Rule, candidate.Matches)).ToArray() ?? [],
            matchingRule is null ? null : RuleSummary.FromRule(matchingRule.Rule, true),
            matchingRule?.Penalty ?? 0);
    }

    private static int CalculateScopeScore(CommercialAnalysisMetricRuleSnapshot rule)
        => (rule.PipelineId is null ? 0 : 1) + (rule.StageId is null ? 0 : 2);

    private static bool MatchesRule(CommercialAnalysisMetricRuleSnapshot rule, decimal value)
        => rule.Operator switch
        {
            ">" => value > ConvertThreshold(rule),
            "<" => value < ConvertThreshold(rule),
            "=" => value == ConvertThreshold(rule),
            ">=" => value >= ConvertThreshold(rule),
            "<=" => value <= ConvertThreshold(rule),
            _ => false
        };

    private static decimal ConvertThreshold(CommercialAnalysisMetricRuleSnapshot rule)
        => rule.ThresholdUnit.ToLowerInvariant() switch
        {
            "hours" => rule.ThresholdValue / 24m,
            "months" => rule.ThresholdValue * 30m,
            _ => rule.ThresholdValue
        };

    private static int ToPenalty(string level)
        => level.ToLowerInvariant() switch
        {
            "critical" => 35,
            "medium" => 18,
            _ => 0
        };

    private static int CalculateConfidenceScore(int ruleCount, int matchedMetricCount, int openActivities, int noteCount)
    {
        var evidenceScore = Math.Min(35, openActivities * 5 + noteCount * 4);
        var ruleScore = ruleCount == 0 ? 0 : Math.Min(45, matchedMetricCount * 12);
        return Math.Clamp(20 + evidenceScore + ruleScore, 0, 100);
    }

    private sealed record RuleMatchCandidate(CommercialAnalysisMetricRuleSnapshot Rule, int ScopeScore, bool Matches, int Penalty);
}
