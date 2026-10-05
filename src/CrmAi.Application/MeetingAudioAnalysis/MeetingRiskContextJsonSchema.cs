namespace CrmAi.Application;

internal static class MeetingRiskContextJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "facts", "confidenceScore", "needsFullTranscript" },
        properties = new
        {
            facts = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    required = new[] { "kind", "description", "participant", "dueAt", "evidenceExcerpt" },
                    properties = new
                    {
                        kind = new { type = "string", @enum = new[] { "decision", "commitment", "objection", "contradiction", "resolution" } },
                        description = new { type = "string" },
                        participant = new { type = new[] { "string", "null" } },
                        dueAt = new { type = new[] { "string", "null" } },
                        evidenceExcerpt = new { type = "string" }
                    }
                }
            },
            confidenceScore = new { type = "integer", minimum = 0, maximum = 100 },
            needsFullTranscript = new { type = "boolean" }
        }
    };
}
