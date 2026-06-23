using CrmAi.Domain;

namespace CrmAi.Application;

public sealed class RiskAnalysisAgent(
    IOpenAiRiskAnalysisClient openAiClient,
    IAiAgentRuntimeSettingsRepository agentSettingsRepository,
    RiskAnalysisAgentInputBuilder inputBuilder) : IRiskAnalysisAgent
{
    private const string AgentKey = "risk-analysis";

    public async Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
    {
        var settings = await agentSettingsRepository.GetAsync(AgentKey, context.Opportunity.CompanyId, cancellationToken);
        var request = inputBuilder.Build(context, settings.ContextEntityKeys);
        if (!settings.IsActive)
        {
            return new RiskAnalysisResult(
                RiskLevel.Low,
                0,
                ["Agent de analise de risco inativo nas configuracoes."],
                [],
                request.SnapshotUpdate);
        }

        var invocationContext = new AiAgentInvocationContext(
            PlatformArea: "opportunity",
            CompanyId: context.Opportunity.CompanyId,
            OpportunityId: context.Opportunity.Id,
            UserId: context.TriggerEvent.UserId ?? context.Opportunity.OwnerUserId,
            ContextEntityKeys: settings.ContextEntityKeys,
            Metadata: new Dictionary<string, object?>
            {
                ["triggerEventId"] = context.TriggerEvent.EventId,
                ["triggerEventType"] = context.TriggerEvent.Type,
                ["stageId"] = context.Opportunity.StageId,
                ["pipelineId"] = context.Opportunity.PipelineId,
                ["accountId"] = context.Opportunity.AccountId
            });

        var agentResponse = await openAiClient.AnalyzeAsync(settings, request.Input, invocationContext, cancellationToken);
        var riskScore = Math.Clamp(agentResponse.RiskScore, 0, 100);

        return new RiskAnalysisResult(
            ParseRiskLevel(agentResponse.RiskLevel, riskScore),
            riskScore,
            Clean(agentResponse.Reasons),
            Clean(agentResponse.Recommendations),
            request.SnapshotUpdate);
    }

    private static RiskLevel ParseRiskLevel(string riskLevel, int riskScore)
        => riskLevel.ToUpperInvariant() switch
        {
            "HIGH" => RiskLevel.High,
            "MEDIUM" => RiskLevel.Medium,
            "LOW" => RiskLevel.Low,
            _ => riskScore >= 70 ? RiskLevel.High : riskScore >= 40 ? RiskLevel.Medium : RiskLevel.Low
        };

    private static IReadOnlyCollection<string> Clean(IReadOnlyCollection<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
