using CrmAi.Application;
using CrmAi.Domain;
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
    public void BeforeScheduledTime_MustRecoverThePreviousDueDate()
    {
        var latestDueDate = PostgresDailyCheckoutSnapshotService.ResolveLatestDueTargetDate(
            new DateTime(2026, 8, 25, 14, 0, 0),
            TimeSpan.FromHours(17.5),
            considerPreviousDayWhenRunBeforeNoon: true);

        Assert.Equal(new DateOnly(2026, 8, 24), latestDueDate);
    }

    [Fact]
    public void AfterScheduledTime_MustIncludeTheCurrentDueDate()
    {
        var latestDueDate = PostgresDailyCheckoutSnapshotService.ResolveLatestDueTargetDate(
            new DateTime(2026, 8, 25, 17, 31, 0),
            TimeSpan.FromHours(17.5),
            considerPreviousDayWhenRunBeforeNoon: true);

        Assert.Equal(new DateOnly(2026, 8, 25), latestDueDate);
    }

    [Fact]
    public void Scheduler_MustBackfillTheEntireOperationalHistoryAndIsolateCompanies()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("ReadFirstOperationalDateAsync", source);
        Assert.Contains("FindMostRecentMissingSnapshotDateAsync", source);
        Assert.Contains("generate_series(@firstDate::date, @latestDate::date", source);
        Assert.DoesNotContain("ScheduledBackfillDays", source);
        Assert.Contains("Failed to generate scheduled daily checkout snapshots for company", source);
    }

    [Fact]
    public void ActiveAgent_MustNotTreatDeterministicFallbackAsCompletedAnalysis()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("not @requireOpenAiAnalysis", source);
        Assert.Contains("payload_json #>> '{executiveSummary,generatedBy}' = 'openai'", source);
        Assert.Contains("RequiresOpenAiAnalysis(agentSettings)", source);
    }

    [Fact]
    public void AgentWithoutApiKey_MustNotCauseEndlessScheduledRetries()
    {
        var settings = new AiAgentRuntimeSettings(
            "daily-checkout",
            IsActive: true,
            "openai",
            "gpt-4.1-mini",
            ApiKey: null,
            "prompt",
            1,
            null,
            []);

        Assert.False(PostgresDailyCheckoutSnapshotService.RequiresOpenAiAnalysis(settings));
    }

    [Fact]
    public void CheckoutSnapshot_MustIncludeNewContactsAndBothOriginBreakdowns()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("as newContacts", source);
        Assert.Contains("left join opportunity_origins", source);
        Assert.Contains("left join contact_origins", source);
        Assert.Contains("coalesce(co.name, nullif(btrim(c.origin), ''), 'Sem origem')", source);
        Assert.Contains("new { key = \"newContacts\"", source);
        Assert.Contains("opportunityOrigins", source);
        Assert.Contains("contactOrigins", source);
    }

    [Fact]
    public void ContactOriginContext_MustRespectAgentContextSelection()
    {
        var input = new DailyCheckoutAnalysisInput(
            new DateOnly(2026, 7, 27),
            new DailyCheckoutSettingsSnapshot("company", "18:00", "America/Sao_Paulo", false),
            new { },
            [],
            new { contactOrigins = new[] { new { label = "Meta Ads", value = 7 } } },
            new { },
            [],
            [],
            []);

        var withoutContacts = PostgresDailyCheckoutSnapshotService.FilterContext(input, ["daily_metrics"]);
        var withContacts = PostgresDailyCheckoutSnapshotService.FilterContext(input, ["daily_metrics", "contacts"]);

        Assert.DoesNotContain("contactOrigins", System.Text.Json.JsonSerializer.Serialize(withoutContacts.Charts));
        Assert.Contains("contactOrigins", System.Text.Json.JsonSerializer.Serialize(withContacts.Charts));
    }

    [Fact]
    public void CheckoutCommercialOutcomes_MustComeFromAuditableTransitions()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("h.event_type = 'status_transition'", source);
        Assert.Contains("h.event_type = 'stage_transition'", source);
        Assert.Contains("advanced_opportunities", source);
        Assert.Contains("Valor potencial que avancou", source);
        Assert.Contains("nao receita", source);
    }

    [Fact]
    public void CheckoutRiskMap_MustUseRiskAgentResults()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/DailyCheckouts/PostgresDailyCheckoutSnapshotService.cs");

        Assert.Contains("i.kind = 'risk-analysis'", source);
        Assert.Contains("as analyzedRisk", source);
        Assert.Contains("risk_payload", source);
        Assert.Contains("Quanto maior, maior a atencao", source);
    }

    [Fact]
    public void CheckoutRecommendations_MustBeActionableAndReferenceExactOpportunities()
    {
        var schema = ReadSource("src/CrmAi.Application/DailyCheckoutJsonSchema.cs");
        var models = ReadSource("src/CrmAi.Domain/DailyCheckoutModels.cs");

        Assert.Contains("\"why\", \"steps\", \"opportunities\"", schema);
        Assert.Contains("\"id\", \"name\", \"reason\", \"approach\"", schema);
        Assert.Contains("DailyCheckoutRecommendationOpportunityResponse", models);
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
