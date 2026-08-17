namespace CrmAi.Application;

public static class CallAudioAnalysisJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[]
        {
            "summary",
            "objections",
            "objectionBreakOpportunities",
            "nextStep",
            "shouldCreateActivity",
            "activityTitle",
            "activityNotes",
            "activityDueAt",
            "confidenceScore",
            "reasons",
            "scorecardItems"
        },
        properties = new
        {
            summary = new { type = "string" },
            objections = new { type = "array", items = new { type = "string" } },
            objectionBreakOpportunities = new { type = "array", items = new { type = "string" } },
            nextStep = new { type = "string" },
            shouldCreateActivity = new
            {
                type = "boolean",
                description = "True only when the call contains a concrete, useful and commercially supported follow-up action."
            },
            activityTitle = new { type = new[] { "string", "null" } },
            activityNotes = new { type = new[] { "string", "null" } },
            activityDueAt = new
            {
                type = new[] { "string", "null" },
                description = "ISO 8601 UTC date/time only when the call supports a deadline; otherwise null."
            },
            confidenceScore = new { type = "integer", minimum = 0, maximum = 100 },
            reasons = new { type = "array", minItems = 1, items = new { type = "string" } },
            scorecardItems = ConversationScorecardJsonSchema.Value
        }
    };
}
