using CrmAi.Infrastructure.SkoposCoach;

namespace CrmAi.Tests;

public sealed class SkoposEvidenceSamplingTests
{
    [Fact]
    public void Sample_balances_opportunity_presence_and_keeps_positive_negative_and_recent_cases()
    {
        var at = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var source = Enumerable.Range(0, 40).Select(i => new SkoposEvidenceSampling.Case(Guid.NewGuid(), $"case-{i}", i < 20, at.AddDays(-i), i % 20 * 5)).ToArray();
        var ids = SkoposEvidenceSampling.Select(source);
        Assert.Equal(30, ids.Length);
        var selected = source.Where(x => ids.Contains(x.Id)).ToArray();
        Assert.Equal(15, selected.Count(x => x.WithoutOpportunity));
        Assert.Equal(30, selected.Select(x => x.CaseKey).Distinct().Count());
        foreach (var group in selected.GroupBy(x => x.WithoutOpportunity))
        {
            Assert.Contains(group, x => x.Score == 0);
            Assert.Contains(group, x => x.Score == 95);
        }
        Assert.Equal(ids, SkoposEvidenceSampling.Select(source.Reverse()));
    }

    [Fact]
    public void Repeated_days_of_one_contact_cannot_dominate_the_prompt()
    {
        var source = Enumerable.Range(0, 100).Select(i => new SkoposEvidenceSampling.Case(Guid.NewGuid(), "same-conversation", true, DateTime.UtcNow.AddDays(-i), null));
        Assert.Equal(2, SkoposEvidenceSampling.Select(source).Length);
    }

    [Theory]
    [InlineData(5, 3, 0, true)]
    [InlineData(200, 1, 20, false)]
    [InlineData(4, 3, 3, false)]
    public void Pdi_readiness_does_not_depend_on_opportunity_count(int reports, int cases, int opportunities, bool expected)
    {
        var metrics = new SkoposIndividualCoachProcessor.ObjectiveMetrics(reports, opportunities, 1, 0, null, null, 0, 0, 0, CaseCount: cases);
        Assert.Equal(expected, SkoposIndividualCoachProcessor.IsReady(metrics));
    }

    [Fact]
    public void Oversized_context_is_rejected_before_a_provider_can_be_called()
    {
        Assert.Throws<SkoposContextBudgetException>(() => SkoposIndividualCoachClient.SerializeBoundedInput(new { text = new string('x', 32_001) }));
    }

    [Fact]
    public void Shared_summary_pool_is_smaller_than_repeating_five_excerpts_in_each_of_seven_competencies()
    {
        var before = System.Text.Json.JsonSerializer.Serialize(new { competencies = Enumerable.Range(0, 7).Select(_ => new { samples = Enumerable.Range(0, 5).Select(_ => new { summary = new string('x', 600) }) }) });
        var limits = SkoposEvidenceSampling.TextLimits(30);
        var after = SkoposIndividualCoachClient.SerializeBoundedInput(new {
            evidenceSample = Enumerable.Range(0, 30).Select(i => new { id = i, summary = new string('x', limits.Summary), competencyKeys = new[] { "service", "cadence" }, criteria = new[] { new { observation = new string('x', limits.Observation) }, new { observation = new string('x', limits.Observation) } } }),
            competencies = Enumerable.Range(0, 7).Select(_ => new { sampledEvidenceCount = 30 }) });
        Assert.True(after.Length < before.Length, $"Thirty shared examples {after.Length}; repeated excerpts {before.Length}");
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(14, 14)]
    [InlineData(15, 15)]
    [InlineData(22, 22)]
    [InlineData(30, 30)]
    [InlineData(80, 30)]
    public void Sample_uses_available_distinct_cases_up_to_thirty_without_manufacturing_the_minimum(int available, int expected)
    {
        var source = Enumerable.Range(0, available).Select(i => new SkoposEvidenceSampling.Case(Guid.NewGuid(), $"case-{i}", i % 2 == 0, DateTime.UtcNow, null));
        Assert.Equal(expected, SkoposEvidenceSampling.Select(source).Length);
    }

    [Fact]
    public void Larger_samples_keep_the_total_excerpt_budget_bounded()
    {
        for (var count = 1; count <= SkoposEvidenceSampling.MaximumReports; count++)
        {
            var limits = SkoposEvidenceSampling.TextLimits(count);
            Assert.InRange(count * (limits.Summary + 2 * limits.Observation), 1, SkoposEvidenceSampling.MaximumExcerptCharacters);
        }
        Assert.Equal((240, 90), SkoposEvidenceSampling.TextLimits(30));
    }

    [Fact]
    public void Methodology_discloses_actual_sample_size_when_below_the_target()
    {
        var metrics = new SkoposIndividualCoachProcessor.ObjectiveMetrics(5, 0, 1, 0, null, null, 0, 0, 0, CaseCount: 3);
        Assert.Contains(SkoposIndividualCoachProcessor.BuildMethodology(metrics, 5), text => text.Contains("15 a 30") && text.Contains("5 disponíveis"));
    }

    [Fact]
    public void Context_budget_shortens_text_without_dropping_thirty_cases_or_changing_scores()
    {
        var input = new {
            evidenceSample = Enumerable.Range(0, 30).Select(i => new {
                id = i, competencyKeys = new[] { "service", "cadence" },
                summary = new string('á', 350),
                criteria = new[] { new { score = 80, reviewed = true, observation = new string('ç', 160) }, new { score = 40, reviewed = false, observation = new string('é', 160) } }
            })
        };
        var json = SkoposIndividualCoachClient.SerializeBoundedInput(input);
        Assert.InRange(json.Length, 1, SkoposIndividualCoachClient.MaximumInputCharacters);
        using var result = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("evidenceTextCompactedForBudget").GetBoolean());
        var samples = result.RootElement.GetProperty("evidenceSample").EnumerateArray().ToArray();
        Assert.Equal(30, samples.Length);
        Assert.Equal(Enumerable.Range(0, 30), samples.Select(x => x.GetProperty("id").GetInt32()));
        Assert.All(samples, sample => {
            Assert.Equal(2, sample.GetProperty("competencyKeys").GetArrayLength());
            Assert.Equal(80, sample.GetProperty("criteria")[0].GetProperty("score").GetInt32());
            Assert.True(sample.GetProperty("criteria")[0].GetProperty("reviewed").GetBoolean());
        });
    }

}
