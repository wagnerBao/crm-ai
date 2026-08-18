using CrmAi.Domain;

namespace CrmAi.Application;

public sealed record OpenAiWhatsappConversationAnalysisResponse(
    string? ConversationSummary,
    bool ShouldCreateNote,
    string? NoteText,
    bool ShouldCreateActivity,
    string? ActivityTitle,
    string? ActivityNotes,
    string? ActivityDueAt,
    int ConfidenceScore,
    IReadOnlyCollection<string> Reasons,
    string? CommercialObservations = null,
    IReadOnlyCollection<string>? NextSteps = null,
    IReadOnlyCollection<string>? Insights = null,
    bool ShouldCreateOpportunity = false,
    string? OpportunityTitle = null,
    string? OpportunityDescription = null,
    string? ActivityMatchingSuggestionId = null,
    string? ActivityIntentKey = null,
    string? OpportunityMatchingSuggestionId = null,
    string? OpportunityIntentKey = null,
    string? MatchingOpenOpportunityId = null,
    IReadOnlyCollection<OpenAiConversationScorecardItem>? ScorecardItems = null);

public sealed record WhatsappScorecardTemplateInput(
    string Id,
    string Name,
    int Version,
    IReadOnlyCollection<WhatsappScorecardCriterionInput> Criteria,
    IReadOnlyCollection<WhatsappPreviousScorecardItemInput> PreviousDailyItems);

public sealed record WhatsappScorecardCriterionInput(
    string Key,
    string Title,
    string? Description,
    decimal Weight,
    string EvaluationInstruction,
    IReadOnlyCollection<string> PositiveExamples,
    IReadOnlyCollection<string> NegativeExamples,
    int ScoreMin,
    int ScoreMax,
    bool IsRequired);

public sealed record WhatsappPreviousScorecardItemInput(
    string CriterionKey,
    int Score,
    int ConfidenceScore,
    string Justification,
    string? Recommendation,
    IReadOnlyCollection<OpenAiConversationEvidence> Evidence);

public sealed record WhatsappScorecardContext(
    string TemplateId,
    string TemplateKey,
    int TemplateVersion,
    string TemplateName,
    IReadOnlyCollection<WhatsappScorecardCriterionContext> Criteria,
    IReadOnlyCollection<WhatsappPreviousScorecardItemInput> PreviousDailyItems)
{
    public WhatsappScorecardTemplateInput ToInput() => new(
        TemplateId,
        TemplateName,
        TemplateVersion,
        Criteria.Select(criterion => new WhatsappScorecardCriterionInput(
            criterion.Key,
            criterion.Title,
            criterion.Description,
            criterion.Weight,
            criterion.EvaluationInstruction,
            criterion.PositiveExamples,
            criterion.NegativeExamples,
            criterion.ScoreMin,
            criterion.ScoreMax,
            criterion.IsRequired)).ToArray(),
        PreviousDailyItems);
}

public sealed record WhatsappScorecardCriterionContext(
    string Id,
    string Key,
    string Title,
    string? Description,
    decimal Weight,
    string EvaluationInstruction,
    IReadOnlyCollection<string> PositiveExamples,
    IReadOnlyCollection<string> NegativeExamples,
    int ScoreMin,
    int ScoreMax,
    bool IsRequired);

public sealed record WhatsappSuggestionSemanticContext(
    IReadOnlyCollection<WhatsappSuggestionCandidate> ExistingSuggestions,
    IReadOnlyCollection<WhatsappOpenOpportunityCandidate> ExistingOpenOpportunities)
{
    public static WhatsappSuggestionSemanticContext Empty { get; } = new([], []);
}

public sealed record WhatsappSuggestionCandidate(
    string Id,
    string SuggestionType,
    string Status,
    string Title,
    string Description,
    DateTime? SuggestedDueAt,
    string? SemanticIntentKey,
    DateTime UpdatedAt);

public sealed record WhatsappOpenOpportunityCandidate(
    string Id,
    string Name,
    string Pipeline,
    string Stage,
    DateTime UpdatedAt);

public sealed record WhatsappConversationAnalysisInput(
    AnalysisWhatsappOpportunity? Opportunity,
    AnalysisWhatsappConversation Conversation,
    AnalysisAccountSummary? Account,
    IReadOnlyCollection<AnalysisProductSummary> Products,
    AnalysisActivitySummary Activities,
    IReadOnlyCollection<AnalysisNoteSummary> RecentNotes,
    IReadOnlyCollection<AnalysisContactSummary> Contacts,
    IReadOnlyCollection<AnalysisUserSummary> Users,
    IReadOnlyCollection<AnalysisHistoryEventSummary> RecentHistoryEvents,
    IReadOnlyCollection<AnalysisAgentInsightSummary> RelatedAgentInsights,
    string? PreviousSummary,
    string NewTranscript,
    string? AdditionalContext,
    DateTime AnalyzedAt)
{
    public IReadOnlyCollection<WhatsappSuggestionCandidate> ExistingSuggestions { get; init; } = [];
    public IReadOnlyCollection<WhatsappOpenOpportunityCandidate> ExistingOpenOpportunities { get; init; } = [];
    public WhatsappScorecardTemplateInput? ScorecardTemplate { get; init; }

    public static WhatsappConversationAnalysisInput FromContext(
        OpportunityAnalysisContext context,
        IReadOnlyCollection<string>? contextEntityKeys = null)
    {
        var data = context.TriggerEvent.Data;
        var entityKeys = new ContextEntitySelection(contextEntityKeys);
        var analyzedAt = DateTime.UtcNow;
        var hasActivities = entityKeys.Has("activities");
        return new WhatsappConversationAnalysisInput(
            entityKeys.Has("opportunity") ? new AnalysisWhatsappOpportunity(
                context.Opportunity.Id,
                context.Opportunity.Name,
                context.Opportunity.Status,
                context.Stage.Title,
                context.Opportunity.Value) : null,
            new AnalysisWhatsappConversation(
                GetString(data, "conversationId") ?? string.Empty,
                GetString(data, "contactId"),
                GetInt(data, "messageCount"),
                GetDateTime(data, "firstMessageAt"),
                GetDateTime(data, "latestMessageAt"),
                GetDateTime(data, "processedUntil")),
            entityKeys.Has("account") && context.Account is not null
                ? new AnalysisAccountSummary(context.Account.Id, context.Account.Name, context.Account.Segment, context.Account.City, context.Account.Uf, context.Account.Status)
                : null,
            entityKeys.Has("products")
                ? context.Products
                    .Select(product => new AnalysisProductSummary(product.Id, product.Name, product.Type, product.Price, product.Featured, product.Status, product.Summary))
                    .ToArray()
                : [],
            new AnalysisActivitySummary(
                hasActivities ? context.Activities.Count(activity => IsPending(activity.Status)) : 0,
                hasActivities ? context.Activities.Count(activity => IsPending(activity.Status) && activity.DateAt.ToUniversalTime() < analyzedAt) : 0,
                hasActivities ? context.Activities
                    .OrderByDescending(activity => activity.DateAt)
                    .Take(15)
                    .Select(activity => new AnalysisActivityItem(
                        activity.Title,
                        activity.ActivityType,
                        activity.Channel,
                        activity.Status,
                        activity.DateAt.ToUniversalTime(),
                        activity.Notes,
                        activity.CompletedNotes,
                        activity.OwnerUserId))
                    .ToArray() : []),
            entityKeys.Has("notes") ? context.Notes
                .OrderByDescending(note => note.CreatedAt)
                .Take(10)
                .Select(note => new AnalysisNoteSummary(note.Text, note.AuthorUserId, note.CreatedAt.ToUniversalTime()))
                .ToArray() : [],
            entityKeys.Has("contacts") ? context.Contacts
                .Select(contact => new AnalysisContactSummary(contact.Name, contact.Role, contact.Status, contact.OwnerUserId))
                .ToArray() : [],
            entityKeys.Has("users") ? context.Users
                .Select(user => new AnalysisUserSummary(user.Name, user.Role, user.IsActive))
                .ToArray() : [],
            entityKeys.Has("history") ? context.HistoryEvents
                .OrderByDescending(history => history.CreatedAt)
                .Take(20)
                .Select(history => new AnalysisHistoryEventSummary(history.Event, history.UserId, history.CreatedAt.ToUniversalTime()))
                .ToArray() : [],
            entityKeys.Has("agent_insights") ? context.AgentInsights
                .OrderByDescending(insight => insight.UpdatedAt)
                .Take(10)
                .Select(insight => new AnalysisAgentInsightSummary(insight.Title, insight.Message, insight.Kind, insight.Confidence, insight.Status, insight.CreatedAt.ToUniversalTime()))
                .ToArray() : [],
            GetString(data, "previousSummary"),
            GetString(data, "text") ?? string.Empty,
            GetString(data, "additionalContext"),
            analyzedAt);
    }

    public static WhatsappConversationAnalysisInput FromContactEvent(OpportunityEvent opportunityEvent)
    {
        var data = opportunityEvent.Data;
        return new WhatsappConversationAnalysisInput(
            null,
            new AnalysisWhatsappConversation(
                GetString(data, "conversationId") ?? string.Empty,
                GetString(data, "contactId"),
                GetInt(data, "messageCount"),
                GetDateTime(data, "firstMessageAt"),
                GetDateTime(data, "latestMessageAt"),
                GetDateTime(data, "processedUntil")),
            null,
            [],
            new AnalysisActivitySummary(0, 0, []),
            [],
            string.IsNullOrWhiteSpace(GetString(data, "contactName"))
                ? []
                : [new AnalysisContactSummary(GetString(data, "contactName")!, string.Empty, "active", GetString(data, "ownerUserId"))],
            [],
            [],
            [],
            GetString(data, "previousSummary"),
            GetString(data, "text") ?? string.Empty,
            GetString(data, "additionalContext"),
            DateTime.UtcNow);
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int GetInt(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;

    private static DateTime? GetDateTime(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) && DateTime.TryParse(value?.ToString(), out var parsed) ? parsed.ToUniversalTime() : null;

    private static bool IsPending(string status)
        => string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    private sealed class ContextEntitySelection(IReadOnlyCollection<string>? keys)
    {
        private readonly HashSet<string> _keys = (keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public bool Has(string key) => _keys.Contains(key);
    }
}

public sealed record AnalysisWhatsappOpportunity(
    string Id,
    string Name,
    string Status,
    string StageTitle,
    decimal Value);

public sealed record AnalysisWhatsappConversation(
    string ConversationId,
    string? ContactId,
    int MessageCount,
    DateTime? FirstMessageAt,
    DateTime? LatestMessageAt,
    DateTime? ProcessedUntil);
