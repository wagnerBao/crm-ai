namespace CrmAi.Application;

public interface IAiAgentInvocationLogStore
{
    Task SaveAsync(AiAgentInvocationLogEntry entry, CancellationToken cancellationToken);
}

public sealed record AiAgentInvocationContext(
    string PlatformArea,
    string? CompanyId = null,
    string? OpportunityId = null,
    string? WhatsappConversationId = null,
    string? MeetingAudioRecordingId = null,
    string? ActivityId = null,
    string? AccountId = null,
    string? ContactId = null,
    string? UserId = null,
    IReadOnlyCollection<string>? ContextEntityKeys = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public static AiAgentInvocationContext Unknown { get; } = new("unknown");
}

public sealed record AiAgentTokenUsage(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CachedPromptTokens,
    int? ReasoningTokens);

public sealed record AiAgentInvocationLogEntry(
    Guid Id,
    string AgentKey,
    string Provider,
    string Model,
    string Operation,
    string PlatformArea,
    string Endpoint,
    int? HttpStatus,
    bool Success,
    string Status,
    string RequestJson,
    string? ResponseJson,
    string? ResultJson,
    string? ErrorType,
    string? ErrorMessage,
    AiAgentTokenUsage Usage,
    AiAgentInvocationContext Context,
    DateTime StartedAt,
    DateTime CompletedAt)
{
    public int DurationMs => Math.Max(0, (int)Math.Round((CompletedAt - StartedAt).TotalMilliseconds));
}

public static class AiAgentInvocationLogStoreExtensions
{
    public static async Task SaveBestEffortAsync(this IAiAgentInvocationLogStore store, AiAgentInvocationLogEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveAsync(entry, cancellationToken);
        }
        catch
        {
            // Auditing must not cause RabbitMQ redelivery loops or duplicate OpenAI calls.
        }
    }
}
