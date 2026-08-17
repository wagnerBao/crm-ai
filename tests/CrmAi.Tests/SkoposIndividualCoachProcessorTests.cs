using CrmAi.Infrastructure.SkoposCoach;

namespace CrmAi.Tests;

public sealed class SkoposIndividualCoachProcessorTests
{
    [Fact]
    public void Coverage_reaches_full_score_only_with_complete_sample()
    {
        var metrics = new SkoposIndividualCoachProcessor.ObjectiveMetrics(
            ReportCount: 10,
            OpportunityCount: 3,
            SourceCount: 2,
            ResponseCount: 8,
            MedianResponseSeconds: 300,
            P90ResponseSeconds: 1800,
            ActivityCount: 12,
            CompletedActivityCount: 10,
            OverdueActivityCount: 1);

        Assert.Equal(100, SkoposIndividualCoachProcessor.Coverage(metrics));
        Assert.Equal(95, SkoposIndividualCoachProcessor.Confidence(metrics));
    }

    [Fact]
    public void Coverage_exposes_missing_response_attribution()
    {
        var metrics = new SkoposIndividualCoachProcessor.ObjectiveMetrics(
            ReportCount: 5,
            OpportunityCount: 3,
            SourceCount: 1,
            ResponseCount: 0,
            MedianResponseSeconds: null,
            P90ResponseSeconds: null,
            ActivityCount: 0,
            CompletedActivityCount: 0,
            OverdueActivityCount: 0);

        Assert.Equal(60, SkoposIndividualCoachProcessor.Coverage(metrics));
        Assert.InRange(SkoposIndividualCoachProcessor.Confidence(metrics), 0, 94);
    }
}
