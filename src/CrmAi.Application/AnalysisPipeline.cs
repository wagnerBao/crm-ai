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
        CancellationToken cancellationToken);
}

public interface IOpenAiWhatsappConversationAnalysisClient
{
    Task<OpenAiWhatsappConversationAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        WhatsappConversationAnalysisInput input,
        CancellationToken cancellationToken);
}

public interface IOpenAiMeetingAudioClient
{
    Task<string> TranscribeAsync(
        AiAgentRuntimeSettings settings,
        string fileName,
        string mimeType,
        byte[] content,
        CancellationToken cancellationToken);

    Task<OpenAiMeetingAudioAnalysisResponse> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        MeetingAudioAnalysisInput input,
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

public interface IWhatsappConversationAnalysisAgent
{
    Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken);
}

public interface IWhatsappConversationActionStore
{
    Task ApplyAsync(OpportunityAnalysisContext context, WhatsappConversationAnalysisResult result, CancellationToken cancellationToken);
}

public interface IWhatsappConversationAnalysisScheduler
{
    Task ScheduleAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OpportunityEvent>> ClaimDueAsync(int limit, CancellationToken cancellationToken);
    Task CompleteAsync(string eventId, CancellationToken cancellationToken);
    Task FailAsync(string eventId, string error, CancellationToken cancellationToken);
}

public interface IMeetingAudioAnalysisService
{
    Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
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
    IMeetingAudioAnalysisService meetingAudioAnalysisService) : IOpportunityAnalysisEventProcessor
{
    public async Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        if (string.Equals(opportunityEvent.Type, "opportunity.meeting_audio.recording.created", StringComparison.OrdinalIgnoreCase))
        {
            await meetingAudioAnalysisService.ProcessAsync(opportunityEvent, cancellationToken);
            return;
        }

        if (string.Equals(opportunityEvent.Type, "opportunity.whatsapp.message.created", StringComparison.OrdinalIgnoreCase))
        {
            await whatsappConversationAnalysisScheduler.ScheduleAsync(opportunityEvent, cancellationToken);
            return;
        }

        var context = await contextRepository.GetForAnalysisAsync(opportunityEvent, cancellationToken);
        if (context is null)
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

        var result = await riskAnalysisAgent.AnalyzeAsync(context, cancellationToken);
        await resultStore.SaveRiskAnalysisAsync(context, result, cancellationToken);
    }
}
