using CrmAi.Domain;

namespace CrmAi.Application;

public sealed class WhatsappConversationAnalysisAgent(
    IOpenAiWhatsappConversationAnalysisClient openAiClient,
    IAiAgentRuntimeSettingsRepository agentSettingsRepository) : IWhatsappConversationAnalysisAgent
{
    private const string AgentKey = "whatsapp-conversation-analysis";

    public async Task<WhatsappConversationAnalysisResult?> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
    {
        var settings = await agentSettingsRepository.GetAsync(AgentKey, context.Opportunity.CompanyId, cancellationToken);
        var input = WhatsappConversationAnalysisInput.FromContext(context, settings.ContextEntityKeys);
        if (string.IsNullOrWhiteSpace(input.NewTranscript))
        {
            return null;
        }

        if (!settings.IsActive)
        {
            return null;
        }

        var invocationContext = new AiAgentInvocationContext(
            PlatformArea: "whatsapp",
            CompanyId: context.Opportunity.CompanyId,
            OpportunityId: context.Opportunity.Id,
            WhatsappConversationId: input.Conversation.ConversationId,
            ContactId: input.Conversation.ContactId,
            UserId: context.TriggerEvent.UserId ?? context.Opportunity.OwnerUserId,
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["triggerEventId"] = context.TriggerEvent.EventId,
                ["triggerEventType"] = context.TriggerEvent.Type,
                ["messageCount"] = input.Conversation.MessageCount,
                ["firstMessageAt"] = input.Conversation.FirstMessageAt,
                ["latestMessageAt"] = input.Conversation.LatestMessageAt,
                ["processedUntil"] = input.Conversation.ProcessedUntil
            });

        return await AnalyzeCoreAsync(settings, input, invocationContext, cancellationToken);
    }

    public async Task<WhatsappConversationAnalysisResult?> AnalyzeContactAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        var companyId = GetString(opportunityEvent, "companyId");
        var settings = await agentSettingsRepository.GetAsync(AgentKey, companyId, cancellationToken);
        var input = WhatsappConversationAnalysisInput.FromContactEvent(opportunityEvent);
        if (string.IsNullOrWhiteSpace(input.NewTranscript) || !settings.IsActive)
        {
            return null;
        }

        var invocationContext = new AiAgentInvocationContext(
            PlatformArea: "whatsapp",
            CompanyId: companyId,
            OpportunityId: null,
            WhatsappConversationId: input.Conversation.ConversationId,
            ContactId: input.Conversation.ContactId,
            UserId: opportunityEvent.UserId ?? GetString(opportunityEvent, "ownerUserId"),
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["triggerEventId"] = opportunityEvent.EventId,
                ["triggerEventType"] = opportunityEvent.Type,
                ["scope"] = "contact",
                ["messageCount"] = input.Conversation.MessageCount,
                ["latestMessageAt"] = input.Conversation.LatestMessageAt
            });

        return await AnalyzeCoreAsync(settings, input, invocationContext, cancellationToken);
    }

    private async Task<WhatsappConversationAnalysisResult> AnalyzeCoreAsync(
        AiAgentRuntimeSettings settings,
        WhatsappConversationAnalysisInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var response = await openAiClient.AnalyzeAsync(settings, input, invocationContext, cancellationToken);
        var dueAt = ParseDateTime(response.ActivityDueAt);

        return new WhatsappConversationAnalysisResult(
            ConversationSummary: CleanText(response.ConversationSummary, input.PreviousSummary ?? input.NewTranscript),
            ShouldCreateNote: response.ShouldCreateNote,
            NoteText: CleanNullableText(response.NoteText),
            ShouldCreateActivity: response.ShouldCreateActivity,
            ActivityTitle: CleanNullableText(response.ActivityTitle),
            ActivityNotes: CleanNullableText(response.ActivityNotes),
            ActivityDueAt: dueAt,
            ConfidenceScore: Math.Clamp(response.ConfidenceScore, 0, 100),
            Reasons: Clean(response.Reasons));
    }

    private static string? GetString(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static string CleanText(string? value, string fallback)
    {
        var normalized = CleanNullableText(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback.Trim() : normalized;
    }

    private static string? CleanNullableText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyCollection<string> Clean(IReadOnlyCollection<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("Conversa analisada e consolidada.")
            .ToArray();
}
