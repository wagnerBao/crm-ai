namespace CrmAi.Tests;

public sealed class AiInsightFailureSafetyTests
{
    [Fact]
    public void Scheduler_Should_Replace_Failed_Payload_Instead_Of_Appending_Provider_Error()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationAnalysisScheduler.cs");
        Assert.Contains("analysis_provider_error", source, StringComparison.Ordinal);
        Assert.Contains("when @status = 'failed'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("message || @error", source, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmAi.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
