namespace CrmAi.Infrastructure.RabbitMq;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string Uri { get; init; } = "amqp://crm_user:crm_pass@localhost:5672/";
    public string OpportunityAnalysisQueue { get; init; } = "crm.projections.opportunity-analysis";
    public string ActivityAnalysisQueue { get; init; } = "crm.projections.activity-analysis";
    public string DailyCheckinQueue { get; init; } = "crm.projections.daily-checkin";
    public string DailyCheckoutQueue { get; init; } = "crm.projections.daily-checkout";
    public string GamificationQueue { get; init; } = "crm.projections.gamification";
    public ushort PrefetchCount { get; init; } = 1;
    public bool RequeueOnFailure { get; init; }
    public string DeadLetterExchange { get; init; } = "crm.projections.dlx";
    public string DeadLetterQueueSuffix { get; init; } = ".dlq";
    public int DailyCheckinSnapshotIntervalMinutes { get; init; } = 15;
    public int DailyCheckoutSnapshotIntervalMinutes { get; init; } = 15;
    public IReadOnlyCollection<string> OpportunityAnalysisExchangeNames { get; init; } =
    [
        "crm.events.opportunity.created",
        "crm.events.opportunity.meeting_audio.recording.created",
        "crm.events.opportunity.note.created",
        "crm.events.opportunity.whatsapp.message.created",
        "crm.events.opportunity.stage.changed",
        "crm.events.opportunity.pipeline.changed",
        "crm.events.opportunity.updated"
    ];
    public IReadOnlyCollection<string> ActivityAnalysisExchangeNames { get; init; } =
    [
        "crm.events.opportunity.activity.created",
        "crm.events.opportunity.activity.updated",
        "crm.events.activity.created",
        "crm.events.activity.updated"
    ];
    public IReadOnlyCollection<string> DailyCheckinExchangeNames { get; init; } =
    [
        "crm.events.opportunity.created",
        "crm.events.opportunity.activity.created",
        "crm.events.opportunity.activity.updated",
        "crm.events.opportunity.note.created",
        "crm.events.opportunity.stage.changed",
        "crm.events.opportunity.pipeline.changed",
        "crm.events.opportunity.updated"
    ];
    public IReadOnlyCollection<string> GamificationExchangeNames { get; init; } =
    [
        "crm.events.opportunity.created",
        "crm.events.opportunity.activity.created",
        "crm.events.opportunity.activity.updated",
        "crm.events.opportunity.note.created",
        "crm.events.opportunity.stage.changed",
        "crm.events.opportunity.pipeline.changed",
        "crm.events.opportunity.updated"
    ];
}
