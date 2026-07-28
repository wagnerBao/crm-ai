using CrmAi.Infrastructure.DailyCheckouts;

namespace CrmAi.Tests;

public sealed class DailyCheckoutSchedulerTests
{
    [Fact]
    public void EveningSchedule_MustAnalyzeTheCurrentDay()
    {
        var localNow = new DateTime(2026, 7, 27, 18, 1, 0);

        var targetDate = PostgresDailyCheckoutSnapshotService.ResolveTargetDate(
            localNow,
            TimeSpan.FromHours(18),
            considerPreviousDayWhenRunBeforeNoon: true);

        Assert.Equal(new DateOnly(2026, 7, 27), targetDate);
    }

    [Fact]
    public void MorningSchedule_CanAnalyzeThePreviousDay()
    {
        var localNow = new DateTime(2026, 7, 28, 8, 1, 0);

        var targetDate = PostgresDailyCheckoutSnapshotService.ResolveTargetDate(
            localNow,
            TimeSpan.FromHours(8),
            considerPreviousDayWhenRunBeforeNoon: true);

        Assert.Equal(new DateOnly(2026, 7, 27), targetDate);
    }

    [Fact]
    public void MorningSchedule_UsesCurrentDayWhenPreviousDayOptionIsDisabled()
    {
        var localNow = new DateTime(2026, 7, 28, 8, 1, 0);

        var targetDate = PostgresDailyCheckoutSnapshotService.ResolveTargetDate(
            localNow,
            TimeSpan.FromHours(8),
            considerPreviousDayWhenRunBeforeNoon: false);

        Assert.Equal(new DateOnly(2026, 7, 28), targetDate);
    }

    [Fact]
    public void ActiveAgent_MustNotTreatDeterministicFallbackAsCompletedAnalysis()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("not @requireOpenAiAnalysis", source);
        Assert.Contains("payload_json #>> '{executiveSummary,generatedBy}' = 'openai'", source);
        Assert.Contains("agentSettings.IsActive", source);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmAi.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
