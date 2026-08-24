using CrmAi.Domain;

namespace CrmAi.Application;

public interface IOpportunityContextRepository
{
    Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent triggerEvent, CancellationToken cancellationToken);
}

public interface IRiskAnalysisAgent
{
    Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken);
}

public interface IOpenAiRiskAnalysisClient
{
    Task<OpenAiRiskAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        RiskAnalysisAgentInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

public interface IOpenAiWhatsappConversationAnalysisClient
{
    Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        WhatsappConversationAnalysisInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

public interface IOpenAiMeetingAudioClient
{
    Task<MeetingAudioTranscriptionResult> TranscribeAsync(
        AiAgentRuntimeSettings settings,
        string fileName,
        string mimeType,
        byte[] content,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);

    Task<OpenAiMeetingAudioAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        MeetingAudioAnalysisInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

public interface IOpenAiDailyCheckoutClient
{
    Task<OpenAiDailyCheckoutResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        DailyCheckoutAnalysisInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

public interface IAiAgentRuntimeSettingsRepository
{
    Task<AiAgentRuntimeSettings> GetAsync(string agentKey, string? companyId, CancellationToken cancellationToken);
}

public interface IAnalysisResultStore
{
    Task SaveRiskAnalysisAsync(OpportunityAnalysisContext context, RiskAnalysisResult result, CancellationToken cancellationToken);
}

public interface IActivityAnalysisEventProcessor
{
    Task ProcessAsync(OpportunityEvent activityEvent, CancellationToken cancellationToken);
}

public interface IWhatsappConversationAnalysisAgent
{
    Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken);
    Task<WhatsappConversationAnalysisResult?> AnalyzeContactAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
        => Task.FromResult<WhatsappConversationAnalysisResult?>(null);
}

public interface IWhatsappSuggestionContextRepository
{
    Task<WhatsappSuggestionSemanticContext> GetAsync(string? companyId, string? contactId, CancellationToken cancellationToken);
}

public interface IWhatsappScorecardContextRepository
{
    Task<WhatsappScorecardContext?> GetAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public interface IWhatsappConversationActionStore
{
    Task ApplyAsync(OpportunityAnalysisContext context, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken);
    Task ApplyContactAsync(OpportunityEvent opportunityEvent, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public interface IWhatsappConversationAnalysisScheduler
{
    Task ScheduleAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OpportunityEvent>> ClaimDueAsync(int limit, CancellationToken cancellationToken);
    Task CompleteAsync(string eventId, CancellationToken cancellationToken);
    Task FailAsync(string eventId, string error, CancellationToken cancellationToken);
}

public interface IInstagramConversationAnalysisService
{
    Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public interface IMeetingAudioAnalysisService
{
    Task<bool> ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public interface IOpportunityAnalysisEventProcessor
{
    Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public interface IDailyCheckinProjectionService
{
    Task ProjectAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
    Task GenerateDailySnapshotsAsync(DateOnly date, CancellationToken cancellationToken);
}

public interface IDailyCheckoutSnapshotService
{
    Task GenerateDueSnapshotsAsync(DateTime utcNow, CancellationToken cancellationToken);
    Task GenerateSnapshotAsync(string companyId, DateOnly? date, CancellationToken cancellationToken);
}

public interface IGamificationProjectionService
{
    Task ProjectAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public sealed class OpportunityAnalysisEventProcessor(
    IOpportunityContextRepository contextRepository,
    IRiskAnalysisAgent riskAnalysisAgent,
    IAnalysisResultStore resultStore,
    IWhatsappConversationAnalysisAgent whatsappConversationAnalysisAgent,
    IWhatsappConversationActionStore whatsappConversationActionStore,
    IWhatsappConversationAnalysisScheduler whatsappConversationAnalysisScheduler,
    IMeetingAudioAnalysisService meetingAudioAnalysisService,
    IInstagramConversationAnalysisService? instagramConversationAnalysisService = null) : IOpportunityAnalysisEventProcessor
{
    public async Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        if (OpportunityEventRouting.IsActivityAnalysisEvent(opportunityEvent.Type))
        {
            return;
        }

        if (string.Equals(opportunityEvent.Type, "opportunity.meeting_audio.recording.created", StringComparison.OrdinalIgnoreCase))
        {
            var transcriptionUpdated = await meetingAudioAnalysisService.ProcessAsync(opportunityEvent, cancellationToken);
            if (!transcriptionUpdated)
            {
                return;
            }
        }

        if (string.Equals(opportunityEvent.Type, "opportunity.whatsapp.message.created", StringComparison.OrdinalIgnoreCase))
        {
            await whatsappConversationAnalysisScheduler.ScheduleAsync(opportunityEvent, cancellationToken);
            return;
        }

        if (string.Equals(opportunityEvent.Type, "opportunity.instagram.message.created", StringComparison.OrdinalIgnoreCase))
        {
            // Individual message events only reset the conversation inactivity clock.
            // The consumers service publishes the consolidated batch after the configured delay.
            return;
        }

        if (string.Equals(opportunityEvent.Type, "opportunity.instagram.conversation.batch", StringComparison.OrdinalIgnoreCase))
        {
            if (instagramConversationAnalysisService is not null)
            {
                await instagramConversationAnalysisService.ProcessAsync(opportunityEvent, cancellationToken);
            }
            return;
        }

        var isWhatsappBatch = string.Equals(
            opportunityEvent.Type,
            "opportunity.whatsapp.conversation.batch",
            StringComparison.OrdinalIgnoreCase);
        var hasOpportunity = Guid.TryParse(opportunityEvent.OpportunityId, out var batchOpportunityId)
            && batchOpportunityId != Guid.Empty;
        if (isWhatsappBatch && !hasOpportunity)
        {
            var contactResult = await whatsappConversationAnalysisAgent.AnalyzeContactAsync(opportunityEvent, cancellationToken);
            if (contactResult is not null)
            {
                await whatsappConversationActionStore.ApplyContactAsync(opportunityEvent, contactResult, cancellationToken);
            }

            return;
        }

        var context = await contextRepository.GetForAnalysisAsync(opportunityEvent, cancellationToken);
        if (context is null)
        {
            return;
        }
        if (!string.Equals(context.Opportunity.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(opportunityEvent.Type, "opportunity.whatsapp.conversation.batch", StringComparison.OrdinalIgnoreCase))
        {
            var whatsappResult = await whatsappConversationAnalysisAgent.AnalyzeAsync(context, cancellationToken);
            if (whatsappResult is not null)
            {
                await whatsappConversationActionStore.ApplyAsync(context, whatsappResult, cancellationToken);
                context = await contextRepository.GetForAnalysisAsync(opportunityEvent, cancellationToken) ?? context;
            }
        }
        if (!string.Equals(context.Opportunity.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = await riskAnalysisAgent.AnalyzeAsync(context, cancellationToken);
        await resultStore.SaveRiskAnalysisAsync(context, result, cancellationToken);
    }
}

public sealed class ActivityAnalysisEventProcessor(
    IOpportunityContextRepository contextRepository,
    IRiskAnalysisAgent riskAnalysisAgent,
    IAnalysisResultStore resultStore) : IActivityAnalysisEventProcessor
{
    public async Task ProcessAsync(OpportunityEvent activityEvent, CancellationToken cancellationToken)
    {
        var context = await contextRepository.GetForAnalysisAsync(activityEvent, cancellationToken);
        if (context is null)
        {
            return;
        }
        if (!string.Equals(context.Opportunity.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = await riskAnalysisAgent.AnalyzeAsync(context, cancellationToken);
        await resultStore.SaveRiskAnalysisAsync(context, result, cancellationToken);
    }
}

public static class OpportunityEventRouting
{
    private static readonly HashSet<string> ActivityAnalysisEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "opportunity.activity.created",
        "opportunity.activity.updated",
        "activity.created",
        "activity.updated"
    };

    public static bool IsActivityAnalysisEvent(string? eventType) =>
        !string.IsNullOrWhiteSpace(eventType) && ActivityAnalysisEvents.Contains(eventType);
}
