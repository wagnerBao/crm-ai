using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrmAi.Application;

public sealed record SuggestionCompletionEvidence(
    string Id,
    string Type,
    DateTime OccurredAt,
    bool BeforeSuggestion,
    string Summary,
    [property: JsonIgnore] string? SourceStreamId = null,
    [property: JsonIgnore] bool IsSynthetic = false);

public sealed record SuggestionCompletionVerificationInput(
    string SuggestionId,
    string SuggestionType,
    string Title,
    string Description,
    DateTime CreatedAt,
    DateTime? DueAt,
    JsonElement Payload,
    IReadOnlyCollection<SuggestionCompletionEvidence> Evidence);

public sealed record SuggestionCompletionVerificationResult(
    string Result,
    int Confidence,
    string Reason,
    IReadOnlyCollection<string> EvidenceIds,
    bool? NeedsFullEvidence = null);

public interface IOpenAiSuggestionCompletionVerificationClient
{
    Task<SuggestionCompletionVerificationResult> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        SuggestionCompletionVerificationInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken);
}

internal static class SuggestionCompletionVerificationJsonSchema
{
    public static object SelectedValue
    {
        get
        {
            var schema = JsonSerializer.SerializeToNode(Value)!.AsObject();
            schema["required"]!.AsArray().Add("needsFullEvidence");
            schema["properties"]!["needsFullEvidence"] = JsonSerializer.SerializeToNode(new { type = "boolean" });
            return schema;
        }
    }

    public static object Value { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "result", "confidence", "reason", "evidenceIds" },
        properties = new
        {
            result = new { type = "string", @enum = new[] { "fulfilled", "unfulfilled", "inconclusive" } },
            confidence = new { type = "integer", minimum = 0, maximum = 100 },
            reason = new { type = "string" },
            evidenceIds = new { type = "array", items = new { type = "string" } }
        }
    };
}

public sealed class OpenAiSuggestionCompletionVerificationClient(
    HttpClient httpClient,
    IOptions<OpenAiRiskAnalysisOptions> options,
    IAiAgentInvocationLogStore invocationLogStore) : IOpenAiSuggestionCompletionVerificationClient
{
    private const string EvidenceIdInstructions = "IDs e1, e2, etc. identificam evidencias desta chamada. Retorne somente IDs de evidence; evidencias anteriores a sugestao sao apenas contexto.";
    private const string FullContextInstructions = "Retorne somente IDs existentes em evidence. Para fulfilled, identifique ao menos uma evidencia posterior a sugestao que comprove a execucao da acao; evidencias anteriores sao apenas contexto.";
    public static string ContextPolicyFingerprint { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", CompactedPromptContext.Version, CompactedPromptContext.ReferenceInstructions, EvidenceIdInstructions, FullContextInstructions,
            SuggestionEvidenceSelectionPolicy.Version, SuggestionEvidenceSelectionPolicy.Instructions))));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SuggestionCompletionVerificationResult> AnalyzeAsync(
        AiAgentRuntimeSettings settings,
        SuggestionCompletionVerificationInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var mode = SuggestionEvidenceSelectionPolicy.ResolveMode(options.Value, invocationContext.CompanyId);
        if (mode == "full") return await AnalyzeAllAsync(settings, input, invocationContext, cancellationToken);
        var selection = SuggestionEvidenceSelector.Select(input);
        var fullJson = JsonSerializer.Serialize(input, JsonOptions);
        var prompt = WithStreamAliases(PreparePrompt(selection.Selected), selection.Selected);
        var baselinePrompt = PreparePrompt(input);
        var selectedInstructions = string.Join("\n\n", new[] { prompt.Instructions, SuggestionEvidenceSelectionPolicy.Instructions }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var baselineSize = baselinePrompt.Json.Length + baselinePrompt.Instructions.Length + (baselinePrompt.Instructions.Length > 0 ? 2 : 0);
        var selectedSize = prompt.Json.Length + selectedInstructions.Length + 2;
        var comparisonId = Guid.NewGuid().ToString("N");
        var baselineContext = SelectionMetrics(invocationContext, selection, mode, "baseline", comparisonId);
        if (!selection.HasSelection || baselineSize - selectedSize < 512 || selectedSize > baselineSize * .9)
            return await AnalyzeAllAsync(settings, input, AddMetrics(baselineContext, new()
                { ["evidenceSelectionSkipped"] = selection.HasSelection ? "insufficient-net-saving" : selection.Reason }), cancellationToken);

        SuggestionCompletionVerificationResult? baseline = null;
        if (mode == "shadow")
            baseline = await AnalyzeAllAsync(settings, input,
                baselineContext, cancellationToken);

        var selectedContext = AddMetrics(SelectionMetrics(invocationContext, selection, mode, "selected", comparisonId), new()
        {
            ["contextOptimizationVersion"] = SuggestionEvidenceSelectionPolicy.Version,
            ["contextMode"] = mode == "shadow" ? "shadow-selected" : "selected",
            ["inputCharactersBefore"] = fullJson.Length,
            ["inputCharactersAfter"] = prompt.Json.Length,
            ["addedInstructionCharacters"] = selectedInstructions.Length + 2,
            ["contextReferenceCount"] = prompt.ReferenceCount
        });
        try
        {
            var response = await AnalyzeRequestAsync(settings, prompt.Json, selectedInstructions, selectedContext, cancellationToken,
                selectedEvidence: true, enrichContext: result =>
                {
                    var restored = Restore(result, prompt.EvidenceIds);
                    var metadata = new Dictionary<string, object?>(selectedContext.Metadata!)
                    {
                        ["selectedEvidenceResultSupported"] = restored is not null && IsSupportedSelection(restored, selection.Selected),
                        ["selectedEvidenceNeedsFullContext"] = result.NeedsFullEvidence
                    };
                    if (baseline is not null)
                    {
                        metadata["verificationBaselineResponse"] = baseline;
                        metadata["verificationResultAgreement"] = restored is not null && baseline.Result == restored.Result;
                        metadata["verificationConfidenceDelta"] = result.Confidence - baseline.Confidence;
                        metadata["verificationEvidenceOverlap"] = restored?.EvidenceIds.Intersect(baseline.EvidenceIds, StringComparer.Ordinal).Count() ?? 0;
                    }
                    return selectedContext with { Metadata = metadata };
                });
            if (baseline is not null) return baseline;
            var restored = Restore(response, prompt.EvidenceIds);
            if (restored is not null && IsSupportedSelection(restored, selection.Selected)) return restored;
        }
        catch (Exception exception) when (mode == "shadow" && exception is not OperationCanceledException)
        {
            // The authoritative full-pool result was obtained before the optional comparison.
            return baseline!;
        }
        catch (Exception exception) when (exception is JsonException or IncompleteVerificationResponseException)
        {
            // Refusal or invalid model output needs the original pool, not a repeated subset.
        }
        var fallbackContext = AddMetrics(SelectionMetrics(invocationContext, selection, mode, "full-fallback", comparisonId), new()
        {
            ["contextOptimizationVersion"] = SuggestionEvidenceSelectionPolicy.Version,
            ["contextMode"] = "selection-full-fallback",
            ["inputCharactersBefore"] = fullJson.Length,
            ["inputCharactersAfter"] = fullJson.Length,
            ["addedInstructionCharacters"] = FullContextInstructions.Length + 2
        });
        var fallback = await AnalyzeRequestAsync(settings, fullJson, FullContextInstructions, fallbackContext, cancellationToken);
        return ValidateResult(fallback, input);
    }

    private static bool IsSupportedSelection(SuggestionCompletionVerificationResult result, SuggestionCompletionVerificationInput input) =>
        result.NeedsFullEvidence == false && result.Confidence >= 90 && result.Result != "inconclusive"
        && IsSupportedResult(result, input)
        && input.Evidence.Any(item => !item.BeforeSuggestion && result.EvidenceIds.Contains(item.Id, StringComparer.Ordinal));

    private static SuggestionCompletionVerificationResult? Restore(SuggestionCompletionVerificationResult result, IReadOnlyDictionary<string, string> ids) =>
        result.EvidenceIds is null ? null : ids.Count == 0 ? result : PromptContextCompaction.RestoreEvidenceIds(result, ids);

    private static AiAgentInvocationContext AddMetrics(AiAgentInvocationContext context, Dictionary<string, object?> values)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata ?? new Dictionary<string, object?>());
        foreach (var (key, value) in values) metadata[key] = value;
        return context with { Metadata = metadata };
    }

    private static AiAgentInvocationContext SelectionMetrics(AiAgentInvocationContext context, SelectedSuggestionEvidence selection,
        string mode, string role, string comparisonId) => context with
    {
        Metadata = new Dictionary<string, object?>(context.Metadata ?? new Dictionary<string, object?>())
        {
            ["evidenceSelectionVersion"] = SuggestionEvidenceSelectionPolicy.Version,
            ["evidenceSelectionMode"] = mode,
            ["evidenceSelectionRole"] = role,
            ["evidenceSelectionComparisonId"] = comparisonId,
            ["evidencePoolCount"] = selection.Original.Evidence.Count,
            ["evidenceSelectedCount"] = selection.Selected.Evidence.Count,
            ["evidenceSentCount"] = role == "selected" ? selection.Selected.Evidence.Count : selection.Original.Evidence.Count,
            ["evidenceOmittedCount"] = selection.OmittedIds.Count
        }
    };

    private static PreparedVerificationPrompt PreparePrompt(SuggestionCompletionVerificationInput input)
    {
        var compact = PromptContextCompaction.Verification(input);
        var instructions = string.Concat(compact.ReferenceCount > 0 ? CompactedPromptContext.ReferenceInstructions + "\n" : "", EvidenceIdInstructions);
        return compact.SavesCharacters(instructions.Length)
            ? new(compact.Json, instructions, compact.EvidenceIds, compact.ReferenceCount)
            : new(JsonSerializer.Serialize(input, JsonOptions), "", new Dictionary<string, string>(), 0);
    }

    private static PreparedVerificationPrompt WithStreamAliases(PreparedVerificationPrompt prompt, SuggestionCompletionVerificationInput input)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(prompt.Json)!;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in input.Evidence)
        {
            if (!string.IsNullOrWhiteSpace(item.SourceStreamId))
            {
                var key = item.Type + ":" + item.SourceStreamId;
                if (!aliases.TryGetValue(key, out var alias))
                {
                    alias = $"c{aliases.Count + 1}";
                    aliases.Add(key, alias);
                }
                root["evidence"]![index]!["stream"] = alias;
            }
            index++;
        }
        return prompt with { Json = root.ToJsonString(JsonOptions) };
    }

    private sealed record PreparedVerificationPrompt(string Json, string Instructions, IReadOnlyDictionary<string, string> EvidenceIds, int ReferenceCount);

    private async Task<SuggestionCompletionVerificationResult> AnalyzeAllAsync(
        AiAgentRuntimeSettings settings,
        SuggestionCompletionVerificationInput input,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken)
    {
        var compact = PromptContextCompaction.Verification(input);
        var compactInstructions = string.Concat(
            compact.ReferenceCount > 0 ? CompactedPromptContext.ReferenceInstructions + "\n" : "",
            EvidenceIdInstructions);
        if (!compact.SavesCharacters(compactInstructions.Length))
        {
            var original = await AnalyzeRequestAsync(settings, JsonSerializer.Serialize(input, JsonOptions), "",
                compact.WithMetrics(invocationContext, "original"), cancellationToken);
            return ValidateResult(original, input);
        }
        var response = await AnalyzeRequestAsync(settings, compact.Json,
            compactInstructions,
            compact.WithMetrics(invocationContext, addedInstructionCharacters: compactInstructions.Length), cancellationToken);
        var restored = PromptContextCompaction.RestoreEvidenceIds(response, compact.EvidenceIds);
        if (restored is not null && IsSupportedResult(restored, input)
            && restored.Result != "inconclusive" && restored.Confidence >= 80)
            return restored;

        // A second pass uses the original, expanded representation when the model cannot
        // confidently interpret the lossless representation. This pass includes the entire collected pool.
        var full = await AnalyzeRequestAsync(settings, JsonSerializer.Serialize(input, JsonOptions), FullContextInstructions,
            compact.WithMetrics(invocationContext, "full-fallback", FullContextInstructions.Length), cancellationToken);
        return ValidateResult(full, input);
    }

    private static SuggestionCompletionVerificationResult ValidateResult(
        SuggestionCompletionVerificationResult result, SuggestionCompletionVerificationInput input) =>
        IsSupportedResult(result, input) ? result : result with
        {
            Result = "inconclusive",
            Confidence = Math.Min(79, Math.Max(0, result.Confidence)),
            Reason = "A resposta da IA nao identificou evidencias validas para sustentar a decisao.",
            EvidenceIds = []
        };

    internal static bool IsSupportedResult(SuggestionCompletionVerificationResult result, SuggestionCompletionVerificationInput input)
    {
        if (result.Result is not ("fulfilled" or "unfulfilled" or "inconclusive")
            || result.EvidenceIds is null || result.Confidence is < 0 or > 100) return false;
        var selected = result.EvidenceIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(id => !input.Evidence.Any(item => item.Id == id))) return false;
        return result.Result != "fulfilled" || input.Evidence.Any(item => selected.Contains(item.Id) && !item.BeforeSuggestion);
    }

    private async Task<SuggestionCompletionVerificationResult> AnalyzeRequestAsync(
        AiAgentRuntimeSettings settings,
        string inputJson,
        string contextInstructions,
        AiAgentInvocationContext invocationContext,
        CancellationToken cancellationToken,
        bool selectedEvidence = false,
        Func<SuggestionCompletionVerificationResult, AiAgentInvocationContext>? enrichContext = null)
    {
        var configured = options.Value;
        var endpoint = configured.ResponsesEndpoint;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? configured.Model : settings.Model;
        var startedAt = DateTime.UtcNow;
        var payload = new
        {
            model,
            reasoning = OpenAiGpt56RequestOptions.Reasoning(model, "low"),
            instructions = string.IsNullOrWhiteSpace(contextInstructions) ? settings.Instructions : $"{settings.Instructions}\n\n{contextInstructions}",
            input = inputJson,
            store = false,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "suggestion_completion_verification",
                    strict = true,
                    schema = selectedEvidence ? SuggestionCompletionVerificationJsonSchema.SelectedValue : SuggestionCompletionVerificationJsonSchema.Value
                }
            }
        };
        var requestJson = JsonSerializer.Serialize(payload, JsonOptions);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var exception = new OpenAiRequestException("OpenAI API key was not configured for suggestion-completion-verification.");
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
            throw exception;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        string responseBody;
        try
        {
            await invocationLogStore.EnsureCreditsAvailableAsync(invocationContext, cancellationToken);
            response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, null, false, requestJson, null, null, exception), cancellationToken);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var exception = new OpenAiRequestException($"OpenAI suggestion completion verification failed with status {(int)response.StatusCode}.", (int)response.StatusCode, responseBody);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw exception;
        }

        try
        {
            var outputText = ExtractOutputText(responseBody);
            var result = JsonSerializer.Deserialize<SuggestionCompletionVerificationResult>(outputText, JsonOptions)
                ?? throw new IncompleteVerificationResponseException("OpenAI response did not match suggestion completion verification schema.");
            if (enrichContext is not null) invocationContext = enrichContext(result);
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, (int)response.StatusCode, true, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), OpenAiInvocationLogBuilder.NormalizeJsonBody(outputText)), cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await invocationLogStore.SaveBestEffortAsync(OpenAiInvocationLogBuilder.Create(settings, configured.Model, "responses.suggestion-completion-verification", endpoint, invocationContext, startedAt, (int)response.StatusCode, false, requestJson, OpenAiInvocationLogBuilder.NormalizeJsonBody(responseBody), null, exception), cancellationToken);
            throw;
        }
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("output", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in items.EnumerateArray())
            {
                if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("content", out var contentItems)
                    || contentItems.ValueKind != JsonValueKind.Array) continue;
                foreach (var content in contentItems.EnumerateArray())
                {
                    if (content.ValueKind == JsonValueKind.Object
                        && content.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "output_text"
                        && content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
                        return text.GetString()!;
                }
            }
        }
        throw new IncompleteVerificationResponseException("OpenAI response did not include output_text.");
    }

    private sealed class IncompleteVerificationResponseException(string message) : InvalidOperationException(message);
}
