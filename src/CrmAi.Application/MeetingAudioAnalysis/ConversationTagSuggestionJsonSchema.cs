namespace CrmAi.Application;

public static class ConversationTagSuggestionJsonSchema
{
    public static object Value { get; } = new
    {
        type = "array",
        maxItems = 5,
        items = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "tagId", "reason", "evidenceExcerpt" },
            properties = new
            {
                tagId = new { type = "string" },
                reason = new { type = "string" },
                evidenceExcerpt = new { type = "string" }
            }
        }
    };
}
