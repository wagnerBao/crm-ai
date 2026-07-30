namespace CrmAi.Tests;

public sealed class DailyCheckoutMetricValidityTests
{
    [Fact]
    public void Snapshot_Should_Respect_The_Date_When_Each_Metric_Became_Effective()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("m.created_at < @endsAt", source);
        Assert.Contains("@startsAt as starts_at", source);
        Assert.Contains("case when m.period = 'monthly' then @monthStartsAt else @startsAt end as starts_at", source);
        Assert.Contains("BuildPerformanceRows", source);
        Assert.Contains("[\"goals\"] = goals", source);
        Assert.Contains("opportunity_state as (", source);
        Assert.Contains("o.status_at_end = 'active'", source);
        Assert.Contains("i.created_at < @endsAt", source);
        Assert.Contains(".Replace(\"\\\\u0000\", string.Empty", source);
        Assert.DoesNotContain("o.status = 'active'", source);
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
