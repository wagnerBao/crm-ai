namespace CrmAi.Application;

internal static class OpenAiGpt56RequestOptions
{
    public static object? Reasoning(string? model, string effort) =>
        IsGpt56(model) ? new { effort } : null;

    internal static bool IsGpt56(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && (string.Equals(model.Trim(), "gpt-5.6", StringComparison.OrdinalIgnoreCase)
            || model.Trim().StartsWith("gpt-5.6-", StringComparison.OrdinalIgnoreCase));
}
