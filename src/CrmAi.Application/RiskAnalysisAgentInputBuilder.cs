using CrmAi.Domain;

namespace CrmAi.Application;

public sealed class RiskAnalysisAgentInputBuilder(CommercialRuleAssessmentService commercialRuleAssessmentService)
{
    public RiskAnalysisAgentRequest Build(OpportunityAnalysisContext context)
    {
        var analyzedAt = context.TriggerEvent.OccurredAt == default
            ? DateTime.UtcNow
            : context.TriggerEvent.OccurredAt.ToUniversalTime();
        var latestInteractionAt = GetLatestInteraction(context);
        var lastInteractionDays = latestInteractionAt.HasValue
            ? DaysBetween(latestInteractionAt.Value, analyzedAt)
            : DaysBetween(context.Opportunity.CreatedAt, analyzedAt);
        var openActivities = context.Activities.Count(activity => IsPending(activity.Status));
        var overdueActivities = context.Activities.Count(activity => IsPending(activity.Status) && activity.DateAt.ToUniversalTime() < analyzedAt);
        var stageChangedAt = GetLastStageChangeAt(context);
        var daysInStage = Math.Max(0, (analyzedAt - (stageChangedAt ?? context.Opportunity.CreatedAt.ToUniversalTime())).Days);
        var interactionCount = context.Notes.Count + context.Activities.Count + context.HistoryEvents.Count;
        var hasStageRegression = HasStageRegression(context);
        var commercialAssessment = commercialRuleAssessmentService.Calculate(
            context,
            daysInStage,
            openActivities,
            overdueActivities,
            interactionCount,
            lastInteractionDays);

        var snapshotUpdate = new OpportunityAnalysisSnapshotUpdate(
            analyzedAt,
            daysInStage,
            openActivities,
            overdueActivities,
            lastInteractionDays,
            latestInteractionAt,
            commercialAssessment.HealthScore,
            commercialAssessment.ConfidenceScore);

        return new RiskAnalysisAgentRequest(
            BuildInput(
                context,
                analyzedAt,
                stageChangedAt,
                latestInteractionAt,
                lastInteractionDays,
                daysInStage,
                openActivities,
                overdueActivities,
                interactionCount,
                hasStageRegression,
                commercialAssessment),
            snapshotUpdate);
    }

    private static RiskAnalysisAgentInput BuildInput(
        OpportunityAnalysisContext context,
        DateTime analyzedAt,
        DateTime? stageChangedAt,
        DateTime? latestInteractionAt,
        int lastInteractionDays,
        int daysInStage,
        int openActivities,
        int overdueActivities,
        int interactionCount,
        bool hasStageRegression,
        CommercialRuleAssessment commercialRuleAssessment)
        => new(
            new AnalysisOpportunitySummary(
                context.Opportunity.Id,
                context.Opportunity.Name,
                context.Opportunity.Status,
                context.Opportunity.Value,
                context.Opportunity.CurrentRiskFlag,
                context.Opportunity.CreatedAt.ToUniversalTime(),
                context.Opportunity.UpdatedAt.ToUniversalTime(),
                context.Opportunity.LastActivityAt?.ToUniversalTime(),
                latestInteractionAt,
                lastInteractionDays,
                interactionCount,
                analyzedAt),
            new AnalysisPipelineSummary(
                context.Opportunity.PipelineId,
                context.Stage.Id,
                context.Stage.Title,
                context.Stage.Position,
                stageChangedAt,
                daysInStage,
                hasStageRegression),
            new AnalysisActivitySummary(
                openActivities,
                overdueActivities,
                context.Activities
                    .OrderByDescending(activity => activity.DateAt)
                    .Take(15)
                    .Select(activity => new AnalysisActivityItem(
                        activity.Title,
                        activity.ActivityType,
                        activity.Channel,
                        activity.Status,
                        activity.DateAt.ToUniversalTime(),
                        activity.Notes,
                        activity.OwnerUserId))
                    .ToArray()),
            context.Notes
                .OrderByDescending(note => note.CreatedAt)
                .Take(10)
                .Select(note => new AnalysisNoteSummary(note.Text, note.AuthorUserId, note.CreatedAt.ToUniversalTime()))
                .ToArray(),
            context.Contacts
                .Select(contact => new AnalysisContactSummary(contact.Name, contact.Role, contact.Status, contact.OwnerUserId))
                .ToArray(),
            context.Users
                .Select(user => new AnalysisUserSummary(user.Name, user.Role, user.IsActive))
                .ToArray(),
            context.HistoryEvents
                .OrderByDescending(history => history.CreatedAt)
                .Take(20)
                .Select(history => new AnalysisHistoryEventSummary(history.Event, history.UserId, history.CreatedAt.ToUniversalTime()))
                .ToArray(),
            new AnalysisTriggerEventSummary(context.TriggerEvent.Type, context.TriggerEvent.OccurredAt.ToUniversalTime(), context.TriggerEvent.UserId),
            commercialRuleAssessment);

    private static DateTime? GetLatestInteraction(OpportunityAnalysisContext context)
    {
        var dates = context.Notes.Select(note => note.CreatedAt)
            .Concat(context.Activities.Select(activity => activity.UpdatedAt))
            .Concat(context.HistoryEvents.Select(history => history.CreatedAt))
            .Concat(context.Opportunity.LastActivityAt is null ? [] : [context.Opportunity.LastActivityAt.Value])
            .Append(context.TriggerEvent.OccurredAt);

        return dates.Max().ToUniversalTime();
    }

    private static DateTime? GetLastStageChangeAt(OpportunityAnalysisContext context)
        => context.HistoryEvents
            .Where(history => history.Event.Contains("stage", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(history => history.CreatedAt)
            .Select(history => (DateTime?)history.CreatedAt.ToUniversalTime())
            .FirstOrDefault();

    private static bool HasStageRegression(OpportunityAnalysisContext context)
        => context.HistoryEvents.Any(history =>
            history.Event.Contains("regress", StringComparison.OrdinalIgnoreCase) ||
            history.Event.Contains("back", StringComparison.OrdinalIgnoreCase) ||
            history.Event.Contains("previous", StringComparison.OrdinalIgnoreCase));

    private static bool IsPending(string status)
        => string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    private static int DaysBetween(DateTime from, DateTime to)
        => Math.Max(0, (int)Math.Floor((to.ToUniversalTime() - from.ToUniversalTime()).TotalDays));
}
