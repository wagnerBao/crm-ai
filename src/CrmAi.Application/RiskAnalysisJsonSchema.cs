namespace CrmAi.Application;

internal static class RiskAnalysisJsonSchema
{
    public static object CompactValue
    {
        get
        {
            var schema = System.Text.Json.JsonSerializer.SerializeToNode(Value)!.AsObject();
            var required = schema["required"]!.AsArray();
            var properties = schema["properties"]!.AsObject();
            foreach (var field in new[] { "contextConfidenceScore", "needsFullMeetingContext", "meetingEvidenceIds" }) required.Add(field);
            properties["contextConfidenceScore"] = System.Text.Json.JsonSerializer.SerializeToNode(new { type = "integer", minimum = 0, maximum = 100 });
            properties["needsFullMeetingContext"] = System.Text.Json.JsonSerializer.SerializeToNode(new { type = "boolean" });
            properties["meetingEvidenceIds"] = System.Text.Json.JsonSerializer.SerializeToNode(new { type = "array", items = new { type = "string" } });
            return schema;
        }
    }

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
