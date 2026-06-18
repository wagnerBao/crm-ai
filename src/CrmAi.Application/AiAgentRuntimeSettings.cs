namespace CrmAi.Application;

public sealed record AiAgentRuntimeSettings(
    string AgentKey,
    bool IsActive,
    string Provider,
    string Model,
    string? ApiKey,
    string SystemPrompt,
    int DebounceMinutes,
    string? ContextInstructions,
    IReadOnlyCollection<string> ContextEntityKeys)
{
    public string Instructions =>
        string.Join("\n\n", new[] { SystemPrompt, ContextInstructions }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public bool HasContext(string key) =>
        ContextEntityKeys.Count == 0 || ContextEntityKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
}
