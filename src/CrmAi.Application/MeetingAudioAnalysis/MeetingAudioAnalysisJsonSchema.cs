namespace CrmAi.Application;

public static class MeetingAudioAnalysisJsonSchema
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
            "confidenceScore",
            "reasons",
            "scorecardItems"
        },
        properties = new
        {
            summary = new { type = "string" },
            objections = new
            {
                type = "array",
                items = new { type = "string" }
            },
            objectionBreakOpportunities = new
            {
                type = "array",
                items = new { type = "string" }
            },
            nextStep = new { type = "string" },
            confidenceScore = new { type = "integer", minimum = 0, maximum = 100 },
            reasons = new { type = "array", minItems = 1, items = new { type = "string" } },
            scorecardItems = ConversationScorecardJsonSchema.Value
        }
    };
}
