using CrmAi.Infrastructure.Persistence;

namespace CrmAi.Tests;

public sealed class PostgresMeetingAudioAnalysisServiceTests
{
    [Theory]
    [InlineData("google_meet", "meeting-service-analysis")]
    [InlineData(null, "meeting-service-analysis")]
    [InlineData("whatsapp_call", "call-audio-analysis")]
    [InlineData("WHATSAPP_CALL", "call-audio-analysis")]
    public void ResolveAgentKey_SelectsSettingsWithoutChangingMeetDefault(string? sourceKind, string expected)
    {
        Assert.Equal(expected, PostgresMeetingAudioAnalysisService.ResolveAgentKey(sourceKind));
    }
}
