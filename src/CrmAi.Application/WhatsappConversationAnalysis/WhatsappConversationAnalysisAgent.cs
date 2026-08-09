using CrmAi.Domain;
using System.Security.Cryptography;
using System.Text;

namespace CrmAi.Application;

public sealed class WhatsappConversationAnalysisAgent(
    IOpenAiWhatsappConversationAnalysisClient openAiClient,
    IAiAgentRuntimeSettingsRepository agentSettingsRepository,
    IWhatsappSuggestionContextRepository suggestionContextRepository) : IWhatsappConversationAnalysisAgent
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

        var semanticContext = await suggestionContextRepository.GetAsync(
            context.Opportunity.CompanyId,
            input.Conversation.ContactId,
            cancellationToken);
        input = input with
        {
            ExistingSuggestions = semanticContext.ExistingSuggestions,
            ExistingOpenOpportunities = semanticContext.ExistingOpenOpportunities
        };

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

        var semanticContext = await suggestionContextRepository.GetAsync(
            companyId,
            input.Conversation.ContactId,
            cancellationToken);
        input = input with
        {
            ExistingSuggestions = semanticContext.ExistingSuggestions,
            ExistingOpenOpportunities = semanticContext.ExistingOpenOpportunities
        };

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
            CommercialObservations: CleanNullableText(response.CommercialObservations),
            NextSteps: CleanOptional(response.NextSteps),
            Insights: CleanOptional(response.Insights),
            ShouldCreateNote: response.ShouldCreateNote,
            NoteText: CleanNullableText(response.NoteText),
            ShouldCreateActivity: response.ShouldCreateActivity,
            ActivityTitle: CleanNullableText(response.ActivityTitle),
            ActivityNotes: CleanNullableText(response.ActivityNotes),
            ActivityDueAt: dueAt,
            ShouldCreateOpportunity: response.ShouldCreateOpportunity,
            OpportunityTitle: CleanNullableText(response.OpportunityTitle),
            OpportunityDescription: CleanNullableText(response.OpportunityDescription),
            ActivityMatchingSuggestionId: ValidateSuggestionMatch(
                response.ActivityMatchingSuggestionId, "activity", input.ExistingSuggestions),
            ActivityIntentKey: CleanIntentKey(response.ActivityIntentKey),
            OpportunityMatchingSuggestionId: ValidateSuggestionMatch(
                response.OpportunityMatchingSuggestionId, "opportunity", input.ExistingSuggestions),
            OpportunityIntentKey: CleanIntentKey(response.OpportunityIntentKey),
            MatchingOpenOpportunityId: ValidateOpenOpportunityMatch(
                response.MatchingOpenOpportunityId, input.ExistingOpenOpportunities),
            ConfidenceScore: Math.Clamp(response.ConfidenceScore, 0, 100),
            Reasons: Clean(response.Reasons),
            GenerationModel: settings.Model,
            PromptFingerprint: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Instructions))).ToLowerInvariant());
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

    private static string? ValidateSuggestionMatch(
        string? value,
        string suggestionType,
        IReadOnlyCollection<WhatsappSuggestionCandidate> candidates)
    {
        if (!Guid.TryParse(value, out var parsed))
        {
            return null;
        }

        var normalized = parsed.ToString();
        return candidates.Any(candidate =>
            candidate.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            && candidate.SuggestionType.Equals(suggestionType, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private static string? ValidateOpenOpportunityMatch(
        string? value,
        IReadOnlyCollection<WhatsappOpenOpportunityCandidate> candidates)
    {
        if (!Guid.TryParse(value, out var parsed))
        {
            return null;
        }

        var normalized = parsed.ToString();
        return candidates.Any(candidate => candidate.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private static string? CleanIntentKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join('_', value.Trim().ToLowerInvariant()
            .Split([' ', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static IReadOnlyCollection<string> Clean(IReadOnlyCollection<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("Conversa analisada e consolidada.")
            .ToArray();

    private static IReadOnlyCollection<string> CleanOptional(IReadOnlyCollection<string>? values)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
