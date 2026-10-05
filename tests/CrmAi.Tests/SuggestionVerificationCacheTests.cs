using CrmAi.Infrastructure.Persistence;

namespace CrmAi.Tests;

public sealed class SuggestionVerificationCacheTests
{
    private static readonly DateTime EvaluatedAt = new(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unchanged_context_is_reusable_for_unfulfilled_and_inconclusive_results_only()
    {
        var input = AiContextOptimizationTests.VerificationInput();
        var settings = AiContextOptimizationTests.Settings();
        var first = SuggestionVerificationCache.Fingerprint(input, settings, settings.Model, EvaluatedAt);
        var same = SuggestionVerificationCache.Fingerprint(input with { Evidence = input.Evidence.Reverse().ToArray() },
            settings with { ContextEntityKeys = settings.ContextEntityKeys.Reverse().ToArray() }, settings.Model, EvaluatedAt.AddMinutes(10));

        Assert.Equal(first, same);
        Assert.True(SuggestionVerificationCache.CanReuse("unfulfilled", first, same));
        Assert.True(SuggestionVerificationCache.CanReuse("inconclusive", first, same));
        foreach (var status in new[] { "fulfilled", "failed", "pending", "processing" })
            Assert.False(SuggestionVerificationCache.CanReuse(status, first, same));
        Assert.False(SuggestionVerificationCache.CanReuse("inconclusive", null, same));
    }

    [Fact]
    public void Changes_to_suggestion_evidence_model_prompt_or_deadline_invalidate_the_cached_result()
    {
        var input = AiContextOptimizationTests.VerificationInput();
        var settings = AiContextOptimizationTests.Settings();
        var baseline = SuggestionVerificationCache.Fingerprint(input, settings, settings.Model, EvaluatedAt);
        foreach (var changed in new[]
        {
            input with { Title = "Nova ação" }, input with { Description = "Objeto diferente" },
            input with { DueAt = input.DueAt!.Value.AddDays(1) },
            input with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { channel = "phone" }) },
            input with { Evidence = input.Evidence.Append(input.Evidence.Last() with { Id = "new", Summary = "Pierre: o problema voltou." }).ToArray() },
            input with { Evidence = input.Evidence.Select(item => item with { Type = "activity", BeforeSuggestion = true }).ToArray() }
        }) Assert.NotEqual(baseline, SuggestionVerificationCache.Fingerprint(changed, settings, settings.Model, EvaluatedAt));

        Assert.NotEqual(baseline, SuggestionVerificationCache.Fingerprint(input, settings, "another-model", EvaluatedAt));
        Assert.NotEqual(baseline, SuggestionVerificationCache.Fingerprint(input, settings with { ContextInstructions = "Nova regra" }, settings.Model, EvaluatedAt));
        Assert.NotEqual(baseline, SuggestionVerificationCache.Fingerprint(input, settings with { ContextEntityKeys = ["notes"] }, settings.Model, EvaluatedAt));
        Assert.NotEqual(baseline, SuggestionVerificationCache.Fingerprint(input, settings, settings.Model, input.DueAt!.Value.AddMinutes(1)));
        Assert.NotEqual(baseline, SuggestionVerificationCache.Fingerprint(input, settings, settings.Model, EvaluatedAt.AddDays(1)));
    }
}
