using CrmAi.Domain;

namespace CrmAi.Application;

public interface IOpportunityContextRepository
{
    Task<OpportunityAnalysisContext?> GetForAnalysisAsync(OpportunityEvent triggerEvent, CancellationToken cancellationToken);
}

public interface IRiskAnalysisAgent
{
    Task<RiskAnalysisResult> AnalyzeAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken);
}

public interface IOpenAiRiskAnalysisClient
{
    Task<OpenAiRiskAnalysisResponse> AnalyzeAsync(
        string instructions,
        RiskAnalysisAgentInput input,
        CancellationToken cancellationToken);
}

public interface IAnalysisResultStore
{
    Task SaveRiskAnalysisAsync(OpportunityAnalysisContext context, RiskAnalysisResult result, CancellationToken cancellationToken);
}

public interface IOpportunityAnalysisEventProcessor
{
    Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public interface IDailyCheckinProjectionService
{
    Task ProjectAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
    Task GenerateDailySnapshotsAsync(DateOnly date, CancellationToken cancellationToken);
}

public interface IGamificationProjectionService
{
    Task ProjectAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
}

public sealed class OpportunityAnalysisEventProcessor(
    IOpportunityContextRepository contextRepository,
    IRiskAnalysisAgent riskAnalysisAgent,
    IAnalysisResultStore resultStore) : IOpportunityAnalysisEventProcessor
{
    public async Task ProcessAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        var context = await contextRepository.GetForAnalysisAsync(opportunityEvent, cancellationToken);
        if (context is null)
        {
            return;
        }

        var result = await riskAnalysisAgent.AnalyzeAsync(context, cancellationToken);
        await resultStore.SaveRiskAnalysisAsync(context, result, cancellationToken);
    }
}
