using CrmAi.Application;

namespace CrmAi.Tests;

public sealed class AiInvocationFailurePolicyTests
{
    [Theory]
    [InlineData(429, "credit_balance_exhausted")]
    [InlineData(429, "insufficient_quota")]
    [InlineData(400, "billing_hard_limit_reached")]
    public void Provider_balance_and_quota_failures_block_new_invocations(int status, string code)
    {
        var result = AiInvocationFailurePolicy.Classify(new OpenAiRequestException("Request failed", status,
            "{\"error\":{\"code\":\"" + code + "\"}}"));

        Assert.Equal(AiInvocationFailureKind.QuotaExhausted, result.Kind);
        Assert.True(result.BlocksFurtherInvocations);
        Assert.Equal(TimeSpan.FromHours(1), result.RetryDelay);
    }

    [Fact]
    public void Missing_key_is_not_treated_as_a_transient_request_failure()
    {
        var result = AiInvocationFailurePolicy.Classify(new OpenAiRequestException(
            "OpenAI API key was not configured for this agent. Set ai_agent_settings.api_key."));

        Assert.Equal(AiInvocationFailureKind.MissingCredential, result.Kind);
        Assert.True(result.BlocksFurtherInvocations);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Rejected_credentials_block_new_invocations(int status)
    {
        var result = AiInvocationFailurePolicy.Classify(new OpenAiRequestException("Request failed", status));

        Assert.Equal(AiInvocationFailureKind.InvalidCredential, result.Kind);
        Assert.True(result.BlocksFurtherInvocations);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public void Transient_http_failures_do_not_block_an_entire_company_agent(int status)
    {
        var result = AiInvocationFailurePolicy.Classify(new OpenAiRequestException("Request failed", status,
            "{\"error\":{\"code\":\"rate_limit_exceeded\"}}"));

        Assert.Equal(AiInvocationFailureKind.Transient, result.Kind);
        Assert.False(result.BlocksFurtherInvocations);
    }

    [Fact]
    public void Wrapped_quota_errors_still_block_new_invocations()
    {
        var result = AiInvocationFailurePolicy.Classify(new InvalidOperationException("Agent failed",
            new OpenAiRequestException("Request failed", 429, "credit_balance_exhausted")));

        Assert.Equal(AiInvocationFailureKind.QuotaExhausted, result.Kind);
    }

    [Fact]
    public void Internal_credits_have_a_separate_reason_from_provider_quota()
    {
        var result = AiInvocationFailurePolicy.Classify(new AiCreditsExhaustedException());
        Assert.Equal(AiInvocationFailureKind.QuotaExhausted, result.Kind);
        Assert.Equal("internal_credits_exhausted", result.Reason);
        Assert.True(result.BlocksFurtherInvocations);
    }
}
