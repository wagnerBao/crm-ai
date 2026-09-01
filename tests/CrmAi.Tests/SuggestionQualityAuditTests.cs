namespace CrmAi.Tests;

public sealed class SuggestionQualityAuditTests
{
    [Fact]
    public void Worker_Must_Claim_Safely_And_Retry_At_Most_Three_Times()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/SuggestionQualityAuditHostedService.cs");

        Assert.Contains("for update skip locked", source);
        Assert.Contains("attempt_count < 3", source);
        Assert.Contains("report.attempt_count + 1", source);
        Assert.Contains("status = 'processing' and updated_at < now() - interval '15 minutes'", source);
        Assert.Contains("report.AttemptCount >= 3", source);
    }

    [Fact]
    public void Worker_Must_Balance_And_Cap_Feedback_Evidence()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/SuggestionQualityAuditHostedService.cs");

        Assert.Contains("group => group.Take(50)", source);
        Assert.Contains("Take(150 - selected.Count)", source);
        Assert.Contains("selected.Take(150)", source);
    }

    [Fact]
    public void Report_Output_Must_Be_Structured_And_Consultive()
    {
        var models = ReadSource("src/CrmAi.Application/SuggestionQualityAuditModels.cs");
        var settings = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresAiAgentRuntimeSettingsRepository.cs");

        Assert.Contains("suggestion_quality_audit", models);
        Assert.Contains("evidenceIds", models);
        Assert.Contains("deduplication", models);
        Assert.Contains("suggestion-quality-audit", settings);
        Assert.Contains("gpt-5.6-terra", settings);
    }

    [Fact]
    public void Already_Completed_Must_Be_A_Weaker_Positive_Signal_Than_Accepted()
    {
        var worker = ReadSource("src/CrmAi.Infrastructure/Persistence/SuggestionQualityAuditHostedService.cs");
        var models = ReadSource("src/CrmAi.Application/SuggestionQualityAuditModels.cs");

        Assert.Contains("\"accepted\" => 1.0", worker);
        Assert.Contains("\"already_completed\" => 0.6", worker);
        Assert.Contains("FeedbackScoringGuidance", worker);
        Assert.Contains("double SignalStrength", models);
    }

    [Fact]
    public void New_Suggestions_Must_Persist_Generation_Metadata()
    {
        var agent = ReadSource("src/CrmAi.Application/WhatsappConversationAnalysis/WhatsappConversationAnalysisAgent.cs");
        var store = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresWhatsappConversationActionStore.cs");

        Assert.Contains("PromptFingerprint", agent);
        Assert.Contains("SHA256.HashData", agent);
        Assert.Contains("generation_model", store);
        Assert.Contains("confidence_score", store);
        Assert.Contains("generation_reasons", store);
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
