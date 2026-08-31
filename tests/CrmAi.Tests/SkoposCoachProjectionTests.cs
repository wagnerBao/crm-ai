using CrmAi.Infrastructure.SkoposCoach;

namespace CrmAi.Tests;

public sealed class SkoposCoachProjectionTests
{
    [Theory]
    [InlineData(5, 2, 70, true)]
    [InlineData(4, 3, 90, false)]
    [InlineData(8, 1, 90, false)]
    [InlineData(8, 3, 69, false)]
    public void Confirmation_requires_all_governance_thresholds(int evidence, int collaborators, int confidence, bool expected) =>
        Assert.Equal(expected, SkoposCoachProjectionService.MeetsConfirmationThreshold(evidence, collaborators, confidence));

    [Fact]
    public void Sanitizer_removes_direct_contact_data_from_model_context()
    {
        var result = SkoposCoachProjectionService.SanitizeForCoach("Retornar para cliente@empresa.com no +55 (11) 99999-1234.");
        Assert.DoesNotContain("cliente@empresa.com", result);
        Assert.DoesNotContain("99999-1234", result);
        Assert.Contains("[email]", result);
        Assert.Contains("[telefone]", result);
    }

    [Fact]
    public void Source_projections_are_isolated_and_publish_explicit_health_states()
    {
        var service = ReadSource("src/CrmAi.Infrastructure/SkoposCoach/SkoposCoachProjectionService.cs");
        var sql = ReadSource("src/CrmAi.Infrastructure/SkoposCoach/SkoposCoachProjectionSql.cs");

        Assert.Contains("Task.WhenAll", service);
        Assert.Contains("ProjectSourceAsync", service);
        Assert.Contains("healthy_no_events", sql);
        Assert.Contains("unconfigured", sql);
        Assert.Contains("disabled", sql);
        Assert.Contains("'error'", sql);
    }

    [Fact]
    public void Collective_schema_requires_team_gap_training_fields_and_exact_evidence_ids()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/SkoposCoach/SkoposCoachSynthesisClient.cs");

        Assert.Contains("gapKey", source);
        Assert.Contains("groupId", source);
        Assert.Contains("targetAudience", source);
        Assert.Contains("durationMinutes", source);
        Assert.Contains("outline", source);
        Assert.Contains("evidenceIds", source);
        Assert.Contains("trends", source);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmAi.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }
}
