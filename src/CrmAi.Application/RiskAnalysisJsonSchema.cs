namespace CrmAi.Application;

internal static class RiskAnalysisJsonSchema
{
    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "riskLevel", "riskScore", "reasons", "recommendations" },
        properties = new
        {
            riskLevel = new { type = "string", @enum = new[] { "LOW", "MEDIUM", "HIGH" } },
            riskScore = new { type = "integer", minimum = 0, maximum = 100 },
            reasons = new
            {
                type = "array",
                minItems = 1,
                items = new { type = "string" }
            },
            recommendations = new
            {
                type = "array",
                minItems = 1,
                items = new { type = "string" }
            }
        }
    };
}
