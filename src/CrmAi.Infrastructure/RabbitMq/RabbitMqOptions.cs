namespace CrmAi.Infrastructure.RabbitMq;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    private string _opportunityAnalysisQueue = "crm.projections.opportunity-analysis";
    private string _activityAnalysisQueue = "crm.projections.activity-analysis";
    private string _dailyCheckinQueue = "crm.projections.daily-checkin";
    private string _dailyCheckoutQueue = "crm.projections.daily-checkout";
    private string _gamificationQueue = "crm.projections.gamification";
    private string _deadLetterExchange = "crm.projections.dlx";
    private string _notificationExchange = "crm.notifications";
    private IReadOnlyCollection<string> _opportunityAnalysisExchangeNames =
    [
        "crm.events.opportunity.created",
        "crm.events.opportunity.meeting_audio.recording.created",
        "crm.events.opportunity.meeting_audio.transcription.updated",
        "crm.events.opportunity.note.created",
        "crm.events.opportunity.whatsapp.message.created",
        "crm.events.opportunity.whatsapp.conversation.batch",
        "crm.events.opportunity.instagram.conversation.batch",
        "crm.events.opportunity.stage.changed",
        "crm.events.opportunity.pipeline.changed",
        "crm.events.opportunity.updated"
    ];
    private IReadOnlyCollection<string> _activityAnalysisExchangeNames =
    [
        "crm.events.opportunity.activity.created",
        "crm.events.opportunity.activity.updated",
        "crm.events.activity.created",
        "crm.events.activity.updated"
    ];
    private IReadOnlyCollection<string> _dailyCheckinExchangeNames =
    [
        "crm.events.opportunity.created",
        "crm.events.opportunity.activity.created",
        "crm.events.opportunity.activity.updated",
        "crm.events.opportunity.note.created",
        "crm.events.opportunity.stage.changed",
        "crm.events.opportunity.pipeline.changed",
        "crm.events.opportunity.updated"
    ];
    private IReadOnlyCollection<string> _gamificationExchangeNames =
    [
        "crm.events.opportunity.created",
        "crm.events.opportunity.activity.created",
        "crm.events.opportunity.activity.updated",
        "crm.events.opportunity.note.created",
        "crm.events.opportunity.stage.changed",
        "crm.events.opportunity.pipeline.changed",
        "crm.events.opportunity.updated"
    ];

    public string Uri { get; init; } = "amqp://crm_user:crm_pass@localhost:5672/";
    public string OpportunityAnalysisQueue { get => RabbitMqEnvironmentName.QueueName(_opportunityAnalysisQueue); init => _opportunityAnalysisQueue = value; }
    public string ActivityAnalysisQueue { get => RabbitMqEnvironmentName.QueueName(_activityAnalysisQueue); init => _activityAnalysisQueue = value; }
    public string DailyCheckinQueue { get => RabbitMqEnvironmentName.QueueName(_dailyCheckinQueue); init => _dailyCheckinQueue = value; }
    public string DailyCheckoutQueue { get => RabbitMqEnvironmentName.QueueName(_dailyCheckoutQueue); init => _dailyCheckoutQueue = value; }
    public string GamificationQueue { get => RabbitMqEnvironmentName.QueueName(_gamificationQueue); init => _gamificationQueue = value; }
    public ushort PrefetchCount { get; init; } = 1;
    public bool RequeueOnFailure { get; init; }
    public string DeadLetterExchange { get => RabbitMqEnvironmentName.ExchangeName(_deadLetterExchange); init => _deadLetterExchange = value; }
    public string NotificationExchange { get => RabbitMqEnvironmentName.ExchangeName(_notificationExchange); init => _notificationExchange = value; }
    public string DeadLetterQueueSuffix { get; init; } = ".dlq";
    public int DailyCheckinSnapshotIntervalMinutes { get; init; } = 15;
    public int DailyCheckoutSnapshotIntervalMinutes { get; init; } = 15;
    public IReadOnlyCollection<string> OpportunityAnalysisExchangeNames { get => PrefixExchangeNames(_opportunityAnalysisExchangeNames); init => _opportunityAnalysisExchangeNames = value; }
    public IReadOnlyCollection<string> ActivityAnalysisExchangeNames { get => PrefixExchangeNames(_activityAnalysisExchangeNames); init => _activityAnalysisExchangeNames = value; }
    public IReadOnlyCollection<string> DailyCheckinExchangeNames { get => PrefixExchangeNames(_dailyCheckinExchangeNames); init => _dailyCheckinExchangeNames = value; }
    public IReadOnlyCollection<string> GamificationExchangeNames { get => PrefixExchangeNames(_gamificationExchangeNames); init => _gamificationExchangeNames = value; }

    private static IReadOnlyCollection<string> PrefixExchangeNames(IReadOnlyCollection<string> exchangeNames) =>
        exchangeNames.Select(RabbitMqEnvironmentName.ExchangeName).ToArray();
}
