namespace CrmAi.Tests;

public sealed class WhatsappCallRabbitMqRoutingTests
{
    [Fact]
    public void OpportunityConsumer_AllowsMeetingAudioWithoutOpportunityId()
    {
        var consumer = ReadSource("src/CrmAi.Infrastructure/OpportunityAnalysis/RabbitMqOpportunityAnalysisConsumer.cs");
        var baseConsumer = ReadSource("src/CrmAi.Infrastructure/RabbitMq/RabbitMqOpportunityEventConsumerBase.cs");

        Assert.Contains("CanProcessWithoutOpportunityId", consumer);
        Assert.Contains("opportunity.meeting_audio.recording.created", consumer);
        Assert.DoesNotContain("sourceKind", consumer);
        Assert.Contains("!CanProcessWithoutOpportunityId(opportunityEvent)", baseConsumer);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmAi.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
