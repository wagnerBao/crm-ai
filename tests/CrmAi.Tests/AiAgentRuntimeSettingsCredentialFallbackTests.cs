namespace CrmAi.Tests;

public sealed class AiAgentRuntimeSettingsCredentialFallbackTests
{
    [Fact]
    public void CallAgent_ReusesCompanyMeetingCredential_WhenItsOwnKeyIsMissing()
    {
        var source = ReadSource("src/CrmAi.Infrastructure/Persistence/PostgresAiAgentRuntimeSettingsRepository.cs");

        Assert.Contains("when settings.agent_key = 'call-audio-analysis'", source, StringComparison.Ordinal);
        Assert.Contains("credential_settings.agent_key = 'meeting-service-analysis'", source, StringComparison.Ordinal);
        Assert.Contains("credential_settings.company_id = @companyId::uuid", source, StringComparison.Ordinal);
        Assert.Contains("credential_settings.company_id is null", source, StringComparison.Ordinal);
        Assert.Contains("lower(btrim(credential_settings.provider)) = lower(btrim(settings.provider))", source, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }

        throw new FileNotFoundException($"Source file not found: {relativePath}");
    }
}
