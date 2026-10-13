namespace CrmAi.Application;

public enum AiInvocationFailureKind
{
    MissingCredential,
    InvalidCredential,
    QuotaExhausted,
    Transient,
    Permanent
}

public sealed record AiInvocationFailure(AiInvocationFailureKind Kind, string Reason, TimeSpan RetryDelay)
{
    public bool BlocksFurtherInvocations => Kind is AiInvocationFailureKind.MissingCredential
        or AiInvocationFailureKind.InvalidCredential or AiInvocationFailureKind.QuotaExhausted;
}

public static class AiInvocationFailurePolicy
{
    public static AiInvocationFailure Classify(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AiCreditsExhaustedException)
                return new(AiInvocationFailureKind.QuotaExhausted, "internal_credits_exhausted", TimeSpan.FromHours(1));
            if (current is not OpenAiRequestException ai) continue;
            var body = ai.ResponseBody ?? ai.Message;
            if (Contains(body, "insufficient_quota") || Contains(body, "credit_balance_exhausted")
                || Contains(body, "credit balance") || Contains(body, "billing_hard_limit_reached"))
                return new(AiInvocationFailureKind.QuotaExhausted, "provider_quota_exhausted", TimeSpan.FromHours(1));
            if (ai.StatusCode is null && Contains(ai.Message, "API key was not configured"))
                return new(AiInvocationFailureKind.MissingCredential, "credential_missing", TimeSpan.FromHours(1));
            if (ai.StatusCode is 401 or 403 || Contains(body, "invalid_api_key"))
                return new(AiInvocationFailureKind.InvalidCredential, "credential_rejected", TimeSpan.FromHours(1));
            return ai.IsTransient
                ? new(AiInvocationFailureKind.Transient, "provider_transient_error", TimeSpan.FromMinutes(5))
                : new(AiInvocationFailureKind.Permanent, "provider_request_rejected", TimeSpan.FromHours(24));
        }

        return new(AiInvocationFailureKind.Transient, "analysis_error", TimeSpan.FromMinutes(5));
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
