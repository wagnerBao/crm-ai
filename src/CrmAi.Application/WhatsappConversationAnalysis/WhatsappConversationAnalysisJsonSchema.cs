namespace CrmAi.Application;

internal static class WhatsappConversationAnalysisJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[]
        {
            "conversationSummary",
            "commercialObservations",
            "nextSteps",
            "insights",
            "shouldCreateNote",
            "noteText",
            "shouldCreateActivity",
            "activityTitle",
            "activityNotes",
            "activityDueAt",
            "shouldCreateOpportunity",
            "opportunityTitle",
            "opportunityDescription",
            "activityMatchingSuggestionId",
            "activityIntentKey",
            "opportunityMatchingSuggestionId",
            "opportunityIntentKey",
            "matchingOpenOpportunityId",
            "requiresSellerResponse",
            "confidenceScore",
            "reasons",
            "scorecardItems"
        },
        properties = new
        {
            conversationSummary = new { type = "string" },
            commercialObservations = new { type = new[] { "string", "null" }, description = "Commercial context, objections, buying signals, constraints and relevant observations not repeated from the summary." },
            nextSteps = new { type = "array", items = new { type = "string" }, description = "Concrete next steps explicitly supported by the conversation." },
            insights = new { type = "array", items = new { type = "string" }, description = "Other useful, non-duplicated insights for the CRM user." },
            shouldCreateNote = new { type = "boolean" },
            noteText = new { type = new[] { "string", "null" } },
            shouldCreateActivity = new { type = "boolean" },
            activityTitle = new { type = new[] { "string", "null" } },
            activityNotes = new { type = new[] { "string", "null" } },
            activityDueAt = new { type = new[] { "string", "null" }, description = "ISO 8601 date/time with the explicit UTC offset of timeZoneId when a follow-up is needed." },
            shouldCreateOpportunity = new { type = "boolean", description = "True only when the conversation contains a concrete commercial opportunity signal." },
            opportunityTitle = new { type = new[] { "string", "null" } },
            opportunityDescription = new { type = new[] { "string", "null" } },
            activityMatchingSuggestionId = new { type = new[] { "string", "null" }, description = "ID from existingSuggestions when an activity has the same underlying business intent; null otherwise." },
            activityIntentKey = new { type = new[] { "string", "null" }, description = "Stable semantic key for the activity intent, independent of wording." },
            opportunityMatchingSuggestionId = new { type = new[] { "string", "null" }, description = "ID from existingSuggestions when an opportunity suggestion has the same underlying business intent; null otherwise." },
            opportunityIntentKey = new { type = new[] { "string", "null" }, description = "Stable semantic key for the opportunity intent, including the material product, vehicle, need or issue." },
            matchingOpenOpportunityId = new { type = new[] { "string", "null" }, description = "ID from existingOpenOpportunities when it already represents the same commercial issue; null otherwise." },
            requiresSellerResponse = new { type = "boolean", description = "True only when the latest customer message contains an unanswered question, request or confirmation that requires a seller reply." },
            confidenceScore = new { type = "integer", minimum = 0, maximum = 100 },
            reasons = new
            {
                type = "array",
                minItems = 1,
                items = new { type = "string" }
            },
            scorecardItems = ConversationScorecardJsonSchema.Value
        }
    };
}
