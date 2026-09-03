namespace CrmAi.Tests;

public sealed class OpenAiDataMinimizationTests
{
    [Fact]
    public void StatelessResponsesRequests_DisableProviderApplicationState()
    {
        var files = new[]
        {
            "src/CrmAi.Application/OpenAiDailyCheckoutClient.cs",
            "src/CrmAi.Application/OpenAiRiskAnalysisClient.cs",
            "src/CrmAi.Application/WhatsappConversationAnalysis/OpenAiWhatsappConversationAnalysisClient.cs",
            "src/CrmAi.Application/SuggestionCompletionVerificationModels.cs",
            "src/CrmAi.Application/SuggestionQualityAuditModels.cs",
            "src/CrmAi.Application/MeetingAudioAnalysis/OpenAiMeetingAudioClient.cs",
            "src/CrmAi.Infrastructure/SkoposCoach/SkoposCoachSynthesisClient.cs",
            "src/CrmAi.Infrastructure/SkoposCoach/SkoposIndividualCoachClient.cs"
        };

        foreach (var file in files)
        {
            Assert.Contains("store = false", ReadSource(file), StringComparison.Ordinal);
        }
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmAi.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.GetFullPath(Path.Combine(directory.FullName, relativePath)));
    }
}
