namespace CrmAi.Application;

internal static class RiskAnalysisJsonSchema
{
    public static object ForContext(bool compact, RealEstateAnalysisInput? realEstate)
    {
        var value = compact ? CompactValue : Value;
        if (realEstate is null) return value;
        var schema = System.Text.Json.JsonSerializer.SerializeToNode(value)!.AsObject();
        schema["required"]!.AsArray().Add("realEstateFindings");
        schema["properties"]!["realEstateFindings"] = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            type = "array", maxItems = realEstate.Evidence.Count == 0 ? 0 : 10,
            items = new
            {
                type = "object", additionalProperties = false,
                required = new[] { "kind", "summary", "recommendation", "evidenceIds" },
                properties = new
                {
                    kind = new { type = "string" }, summary = new { type = "string" }, recommendation = new { type = "string" },
                    evidenceIds = new { type = "array", minItems = 1, maxItems = 5, items = new { type = "string" } }
                }
            }
        });
        return schema;
    }

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
