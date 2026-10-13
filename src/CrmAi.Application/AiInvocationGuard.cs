namespace CrmAi.Application;

public sealed class AiInvocationDeferredException(DateTime retryAtUtc, string reason, Exception? innerException = null)
    : Exception($"AI invocation deferred until {retryAtUtc:O}: {reason}.", innerException)
{
    public DateTime RetryAtUtc { get; } = retryAtUtc;
    public string Reason { get; } = reason;
}

public sealed record AiInvocationPermit(Guid? ProbeId = null);

public interface IAiInvocationGuard
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<AiInvocationPermit> EnsureAvailableAsync(AiAgentRuntimeSettings settings, string? companyId, CancellationToken cancellationToken);
    Task<AiInvocationDeferredException?> RecordFailureAsync(AiAgentRuntimeSettings settings, string? companyId,
        Exception exception, CancellationToken cancellationToken);
    Task RecordSuccessAsync(AiAgentRuntimeSettings settings, string? companyId, CancellationToken cancellationToken,
        AiInvocationPermit? permit = null);
}
