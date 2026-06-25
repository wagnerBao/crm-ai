namespace CrmAi.Application;

internal static class DailyCheckoutJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "executiveSummary", "alerts", "recommendations" },
        properties = new
        {
            executiveSummary = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "headline", "focus" },
                properties = new
                {
                    headline = new { type = "string" },
                    focus = new { type = "string" }
                }
            },
            alerts = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "title", "description", "severity" },
                    properties = new
                    {
                        title = new { type = "string" },
                        description = new { type = "string" },
                        severity = new { type = "string", @enum = new[] { "low", "medium", "high", "critical" } }
                    }
                }
            },
            recommendations = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "title", "description", "priority" },
                    properties = new
                    {
                        title = new { type = "string" },
                        description = new { type = "string" },
                        priority = new { type = "string", @enum = new[] { "next_round", "high", "medium", "low" } }
                    }
                }
            }
        }
    };
}

