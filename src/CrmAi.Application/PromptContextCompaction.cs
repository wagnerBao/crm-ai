using System.Text.Json;
using System.Text.Json.Nodes;
using CrmAi.Domain;

namespace CrmAi.Application;

internal sealed record CompactedPromptContext(
    string Json,
    int OriginalCharacters,
    int ReferenceCount,
    IReadOnlyDictionary<string, string> EvidenceIds)
{
    public const string Version = "lossless-context-v1";
    public const string ReferenceInstructions = """
        Formato compacto do contexto:
        Objetos {"$ref":"#/caminho/indice"} reutilizam exatamente o valor nesse caminho do mesmo JSON. Considere todos os fatos, participantes, datas e contradicoes; referencias nao sao eventos adicionais.
        """;

    public bool SavesCharacters(int addedInstructionCharacters) =>
        Json.Length + addedInstructionCharacters + (addedInstructionCharacters > 0 ? 2 : 0) < OriginalCharacters;

    public AiAgentInvocationContext WithMetrics(AiAgentInvocationContext context, string mode = "compact", int addedInstructionCharacters = 0) => context with
    {
        Metadata = new Dictionary<string, object?>(context.Metadata ?? new Dictionary<string, object?>())
        {
            ["contextOptimizationVersion"] = Version,
            ["contextMode"] = mode,
            ["inputCharactersBefore"] = OriginalCharacters,
            ["inputCharactersAfter"] = mode == "compact" ? Json.Length : OriginalCharacters,
            ["addedInstructionCharacters"] = addedInstructionCharacters + (addedInstructionCharacters > 0 ? 2 : 0),
            ["contextReferenceCount"] = mode == "compact" ? ReferenceCount : 0
        }
    };
}

internal static class PromptContextCompaction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static CompactedPromptContext DailyCheckout(DailyCheckoutAnalysisInput input)
    {
        var root = JsonSerializer.SerializeToNode(input, JsonOptions)!.AsObject();
        var original = root.ToJsonString(JsonOptions);
        var references = 0;
        foreach (var (field, table) in new[]
                 { ("updatedOpportunities", "movements"), ("riskItems", "focus"), ("lowEffectiveness", "lowEffectiveness") })
        {
            if (root[field] is not JsonArray rows || root["tables"]?[table] is not JsonArray source) continue;
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is not JsonObject) continue;
                for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
                {
                    if (!JsonNode.DeepEquals(rows[index], source[sourceIndex])) continue;
                    var reference = Reference($"#/tables/{table}/{sourceIndex}");
                    if (reference.ToJsonString(JsonOptions).Length < rows[index]!.ToJsonString(JsonOptions).Length)
                    {
                        rows[index] = reference;
                        references++;
                    }
                    break;
                }
            }
        }
        return new(root.ToJsonString(JsonOptions), original.Length, references, new Dictionary<string, string>());
    }

    public static CompactedPromptContext Verification(SuggestionCompletionVerificationInput input)
    {
        var root = JsonSerializer.SerializeToNode(input, JsonOptions)!.AsObject();
        var original = root.ToJsonString(JsonOptions);
        var references = 0;
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var summaries = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = root["evidence"]!.AsArray();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]!.AsObject();
            var alias = $"e{index + 1}";
            ids.Add(alias, row["id"]!.GetValue<string>());
            row["id"] = alias;
            var summary = row["summary"]!.GetValue<string>();
            if (summaries.TryGetValue(summary, out var sourceIndex))
            {
                var reference = Reference($"#/evidence/{sourceIndex}/summary");
                if (reference.ToJsonString(JsonOptions).Length < row["summary"]!.ToJsonString(JsonOptions).Length)
                {
                    row["summary"] = reference;
                    references++;
                }
            }
            else summaries.Add(summary, index);
        }
        // Notes in suggestion payloads often repeat the entire description. Keep the field
        // and its meaning while sending the text just once.
        if (root["payload"] is JsonObject payload)
        {
            foreach (var field in new[] { "notes", "description" })
            {
                if (payload[field] is not JsonValue text || !text.TryGetValue<string>(out var value)
                    || !string.Equals(value, input.Description, StringComparison.Ordinal)) continue;
                var reference = Reference("#/description");
                if (reference.ToJsonString(JsonOptions).Length >= text.ToJsonString(JsonOptions).Length) continue;
                payload[field] = reference;
                references++;
            }
        }
        return new(root.ToJsonString(JsonOptions), original.Length, references, ids);
    }

    public static SuggestionCompletionVerificationResult? RestoreEvidenceIds(
        SuggestionCompletionVerificationResult result,
        IReadOnlyDictionary<string, string> ids)
    {
        var restored = new List<string>();
        foreach (var id in result.EvidenceIds)
        {
            if (!ids.TryGetValue(id, out var original)) return null;
            restored.Add(original);
        }
        return result with { EvidenceIds = restored.Distinct(StringComparer.Ordinal).ToArray() };
    }

    private static JsonObject Reference(string path) => new() { ["$ref"] = path };
}
