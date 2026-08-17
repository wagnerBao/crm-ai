namespace CrmAi.Application;

public static class ConversationScorecardJsonSchema
{
    public static object Value { get; } = new
    {
        type = "array",
        description = "One item for every criterion supplied in input.scorecardTemplate. Return an empty array when no template was supplied.",
        items = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "criterionKey", "score", "confidenceScore", "justification", "recommendation", "evidence" },
            properties = new
            {
                criterionKey = new { type = "string" },
                score = new { type = "integer", minimum = 0, maximum = 100 },
                confidenceScore = new { type = "integer", minimum = 0, maximum = 100 },
                justification = new { type = "string" },
                recommendation = new { type = new[] { "string", "null" } },
                evidence = new
                {
                    type = "array",
                    maxItems = 5,
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "excerpt", "participant", "startMs", "endMs", "source", "confidenceScore" },
                        properties = new
                        {
                            excerpt = new { type = "string" },
                            participant = new { type = new[] { "string", "null" } },
                            startMs = new { type = new[] { "integer", "null" }, minimum = 0 },
                            endMs = new { type = new[] { "integer", "null" }, minimum = 0 },
                            source = new { type = "string", @enum = new[] { "transcript" } },
                            confidenceScore = new { type = "integer", minimum = 0, maximum = 100 }
                        }
                    }
                }
            }
        }
    };
}
