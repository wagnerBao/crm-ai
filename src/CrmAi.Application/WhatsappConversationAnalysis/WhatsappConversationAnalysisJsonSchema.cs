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
            "shouldCreateNote",
            "noteText",
            "shouldCreateActivity",
            "activityTitle",
            "activityNotes",
            "activityDueAt",
            "confidenceScore",
            "reasons"
        },
        properties = new
        {
            conversationSummary = new { type = "string" },
            shouldCreateNote = new { type = "boolean" },
            noteText = new { type = new[] { "string", "null" } },
            shouldCreateActivity = new { type = "boolean" },
            activityTitle = new { type = new[] { "string", "null" } },
            activityNotes = new { type = new[] { "string", "null" } },
            activityDueAt = new { type = new[] { "string", "null" }, description = "ISO 8601 UTC date/time when a follow-up is needed." },
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
