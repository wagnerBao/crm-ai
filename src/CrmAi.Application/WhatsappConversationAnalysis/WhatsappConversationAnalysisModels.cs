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
    IReadOnlyCollection<string> Reasons);

public sealed record WhatsappConversationAnalysisInput(
    AnalysisWhatsappOpportunity Opportunity,
    AnalysisWhatsappConversation Conversation,
    string? PreviousSummary,
    string NewTranscript,
    string? AdditionalContext,
    DateTime AnalyzedAt)
{
    public static WhatsappConversationAnalysisInput FromContext(OpportunityAnalysisContext context)
    {
        var data = context.TriggerEvent.Data;
        return new WhatsappConversationAnalysisInput(
            new AnalysisWhatsappOpportunity(
                context.Opportunity.Id,
                context.Opportunity.Name,
                context.Opportunity.Status,
                context.Stage.Title,
                context.Opportunity.Value),
            new AnalysisWhatsappConversation(
                GetString(data, "conversationId") ?? string.Empty,
                GetString(data, "contactId"),
                GetInt(data, "messageCount"),
                GetDateTime(data, "firstMessageAt"),
                GetDateTime(data, "latestMessageAt"),
                GetDateTime(data, "processedUntil")),
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
