using FluentAssertions;

namespace CrmAi.Tests;

public sealed class DailyCheckoutMetricValidityTests
{
    [Fact]
    public void Snapshot_Should_Respect_The_Date_When_Each_Metric_Became_Effective()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        source.Should().Contain("m.created_at < @endsAt");
        source.Should().Contain("greatest(@startsAt, m.created_at) as starts_at");
        source.Should().Contain("greatest(case when m.period = 'monthly' then @monthStartsAt else @startsAt end, m.created_at)");
        source.Should().Contain("BuildPerformanceRows");
        source.Should().Contain("[\"goals\"] = goals");
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
