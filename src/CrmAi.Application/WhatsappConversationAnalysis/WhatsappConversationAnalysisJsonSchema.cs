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
            "confidenceScore",
            "reasons"
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
            activityDueAt = new { type = new[] { "string", "null" }, description = "ISO 8601 UTC date/time when a follow-up is needed." },
            shouldCreateOpportunity = new { type = "boolean", description = "True only when the conversation contains a concrete commercial opportunity signal." },
            opportunityTitle = new { type = new[] { "string", "null" } },
            opportunityDescription = new { type = new[] { "string", "null" } },
            confidenceScore = new { type = "integer", minimum = 0, maximum = 100 },
            reasons = new
            {
                type = "array",
                minItems = 1,
                items = new { type = "string" }
            }
        }
    };
}
