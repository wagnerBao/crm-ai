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
            "nextStep"
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
            nextStep = new { type = "string" }
        }
    };
}
