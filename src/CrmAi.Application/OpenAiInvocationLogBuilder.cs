using System.Text.Json;

namespace CrmAi.Application;

internal static class OpenAiInvocationLogBuilder
{
    public static string NormalizeJsonBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { raw = body }, JsonSerializerOptions.Web);
        }
    }

    public static AiAgentTokenUsage ExtractUsage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return EmptyUsage();
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("usage", out var usage))
            {
                return EmptyUsage();
            }

            return new AiAgentTokenUsage(
                PromptTokens: GetInt(usage, "input_tokens") ?? GetInt(usage, "prompt_tokens"),
                CompletionTokens: GetInt(usage, "output_tokens") ?? GetInt(usage, "completion_tokens"),
                TotalTokens: GetInt(usage, "total_tokens"),
                CachedPromptTokens: ReadNestedInt(usage, "input_tokens_details", "cached_tokens")
                    ?? ReadNestedInt(usage, "prompt_tokens_details", "cached_tokens"),
                ReasoningTokens: ReadNestedInt(usage, "output_tokens_details", "reasoning_tokens")
                    ?? ReadNestedInt(usage, "completion_tokens_details", "reasoning_tokens"));
        }
        catch (JsonException)
        {
            return EmptyUsage();
        }
    }

    public static AiAgentInvocationLogEntry Create(
        AiAgentRuntimeSettings settings,
        string fallbackModel,
        string operation,
        string endpoint,
        AiAgentInvocationContext context,
        DateTime startedAt,
        int? httpStatus,
        bool success,
        string requestJson,
        string? responseJson,
        string? resultJson,
        Exception? exception = null,
        string? modelOverride = null)
    {
        var completedAt = DateTime.UtcNow;
        return new AiAgentInvocationLogEntry(
            Guid.NewGuid(),
            settings.AgentKey,
            string.IsNullOrWhiteSpace(settings.Provider) ? "openai" : settings.Provider,
            FirstConfiguredValue(modelOverride, settings.Model, fallbackModel),
            operation,
            context.PlatformArea,
            endpoint,
            httpStatus,
            success,
            success ? "success" : "error",
            requestJson,
            responseJson,
            resultJson,
            exception?.GetType().Name,
            exception?.Message,
            ExtractUsage(responseJson),
            context,
            startedAt,
            completedAt);
    }

    public static AiAgentTokenUsage EmptyUsage() => new(null, null, null, null, null);

    private static string FirstConfiguredValue(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "unknown";

    private static int? ReadNestedInt(JsonElement root, string objectName, string propertyName) =>
        root.TryGetProperty(objectName, out var nested) ? GetInt(nested, propertyName) : null;

    private static int? GetInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }
}
