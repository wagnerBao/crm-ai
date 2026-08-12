namespace CrmAi.Tests;

public sealed class WhatsappCallRabbitMqRoutingTests
{
    [Fact]
    public void OpportunityConsumer_AllowsWhatsappCallWithoutOpportunityId()
    {
        var consumer = ReadSource("src/CrmAi.Infrastructure/OpportunityAnalysis/RabbitMqOpportunityAnalysisConsumer.cs");
        var baseConsumer = ReadSource("src/CrmAi.Infrastructure/RabbitMq/RabbitMqOpportunityEventConsumerBase.cs");

        Assert.Contains("CanProcessWithoutOpportunityId", consumer);
        Assert.Contains("sourceKind", consumer);
        Assert.Contains("whatsapp_call", consumer);
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
