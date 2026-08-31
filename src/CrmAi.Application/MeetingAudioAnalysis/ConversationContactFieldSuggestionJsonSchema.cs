namespace CrmAi.Application;

public static class ConversationContactFieldSuggestionJsonSchema
{
    public static object Value { get; } = new
    {
        type = "array",
        maxItems = 5,
        items = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "fieldId", "value", "reason", "evidenceExcerpt" },
            properties = new
            {
                fieldId = new { type = "string" },
                value = new { type = "string", maxLength = 2000 },
                reason = new { type = "string" },
                evidenceExcerpt = new { type = "string" }
            }
        }
    };
}
