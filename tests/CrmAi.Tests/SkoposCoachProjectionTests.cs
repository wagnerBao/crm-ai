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
}
