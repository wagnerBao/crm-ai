using CrmAi.Domain;

namespace CrmAi.Application;

public interface IRiskAnalysisScheduler
{
    Task ScheduleAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken);
}

// A claim identifies a specific revision. Later events must survive completion of this claim.
public sealed record RiskAnalysisClaim(Guid CompanyId, Guid OpportunityId, Guid LeaseId, long Revision,
    OpportunityEvent Event);

public sealed record RiskAnalysisRequest(Guid CompanyId, Guid OpportunityId, OpportunityEvent Event, int DelayMinutes);
public sealed record RiskAnalysisWork(Guid CompanyId, Guid OpportunityId, long Revision, OpportunityEvent Event, DateTime NotBeforeUtc);
public enum RiskClaimStatus { Acquired, Obsolete, Wait, Exhausted }
public sealed record RiskClaimResult(RiskClaimStatus Status, RiskAnalysisClaim? Claim = null, DateTime? RetryAtUtc = null, int Attempts = 0);

public interface IRiskAnalysisState
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<RiskAnalysisWork?> RegisterAsync(RiskAnalysisRequest request, CancellationToken cancellationToken);
    Task<RiskClaimResult> AcquireAsync(RiskAnalysisWork work, CancellationToken cancellationToken);
    Task CompleteAsync(RiskAnalysisClaim claim, string? error, CancellationToken cancellationToken);
    Task RetryAsync(RiskAnalysisClaim claim, string error, CancellationToken cancellationToken);
}

public interface IRiskAnalysisMessages
{
    void PublishRequest(RiskAnalysisRequest request);
    void PublishWork(RiskAnalysisWork work);
    void PublishFailure(RiskAnalysisWork work, string error);
}

public sealed class RabbitRiskAnalysisScheduler(IAiAgentRuntimeSettingsRepository settingsRepository,
    IRiskAnalysisMessages messages) : IRiskAnalysisScheduler
{
    public async Task ScheduleAsync(OpportunityAnalysisContext context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Opportunity.CompanyId, out var company)
            || !Guid.TryParse(context.Opportunity.Id, out var opportunity)) return;
        var settings = await settingsRepository.GetAsync("risk-analysis", context.Opportunity.CompanyId, cancellationToken);
        if (!settings.IsActive) return;
        cancellationToken.ThrowIfCancellationRequested();
        // Publisher confirmation precedes acknowledgement of the originating CRM event.
        messages.PublishRequest(new(company, opportunity, context.TriggerEvent with { OpportunityId = context.Opportunity.Id, Data = new Dictionary<string, object?>() },
            Math.Clamp(settings.DebounceMinutes, 1, 1440)));
    }
}

public sealed class RiskAnalysisMessageHandler(IRiskAnalysisState state, IRiskAnalysisMessages messages,
    ScheduledRiskAnalysisProcessor processor)
{
    public async Task ScheduleAsync(RiskAnalysisRequest request, CancellationToken cancellationToken)
    {
        var work = await state.RegisterAsync(request, cancellationToken);
        // A redelivered request republishes the SAME version and deadline. This recovers a crash
        // between the state commit and publication without an outbox scan or losing the wakeup.
        if (work is not null) messages.PublishWork(work);
    }

    public async Task ExecuteAsync(RiskAnalysisWork work, CancellationToken cancellationToken)
    {
        if (work.NotBeforeUtc > DateTime.UtcNow)
        {
            messages.PublishWork(work); // Continue waiting in RabbitMQ, without a database query.
            return;
        }
        var result = await state.AcquireAsync(work, cancellationToken);
        if (result.Status == RiskClaimStatus.Obsolete) return;
        if (result.Status == RiskClaimStatus.Wait)
        {
            messages.PublishWork(work with { NotBeforeUtc = result.RetryAtUtc!.Value });
            return;
        }
        if (result.Status == RiskClaimStatus.Exhausted)
        {
            messages.PublishFailure(work, "Risk analysis retry limit reached.");
            return;
        }
        var claim = result.Claim!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5)); // Lease is ten minutes.
            await processor.ProcessAsync(claim, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (CanRetry(exception) && result.Attempts < 3)
            {
                await state.RetryAsync(claim, exception.Message, cancellationToken);
                messages.PublishWork(work with { NotBeforeUtc = DateTime.UtcNow.AddMinutes(5) });
            }
            else
            {
                messages.PublishFailure(work, exception.Message);
                await state.CompleteAsync(claim, exception.Message, cancellationToken);
            }
            return;
        }
        // If persistence fails after the provider responded, keep the delivery unacknowledged.
        await state.CompleteAsync(claim, null, cancellationToken);
    }

    internal static bool CanRetry(Exception exception)
    {
        if (exception is not OpenAiRequestException ai) return true;
        var body = ai.ResponseBody ?? ai.Message;
        return ai.IsTransient && !body.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
            && !body.Contains("credit_balance_exhausted", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ScheduledRiskAnalysisProcessor(
    IOpportunityContextRepository contextRepository,
    IAiAgentRuntimeSettingsRepository settingsRepository,
    IRiskAnalysisAgent riskAgent,
    IAnalysisResultStore resultStore)
{
    public async Task ProcessAsync(RiskAnalysisClaim claim, CancellationToken cancellationToken)
    {
        // Reload after the quiet period; never analyze the snapshot held by the event consumer.
        var context = await contextRepository.GetForAnalysisAsync(claim.Event, cancellationToken);
        if (context is null
            || !Guid.TryParse(context.Opportunity.CompanyId, out var companyId) || companyId != claim.CompanyId
            || !Guid.TryParse(context.Opportunity.Id, out var opportunityId) || opportunityId != claim.OpportunityId
            || !string.Equals(context.Opportunity.Status, "active", StringComparison.OrdinalIgnoreCase)) return;
        var settings = await settingsRepository.GetAsync("risk-analysis", context.Opportunity.CompanyId, cancellationToken);
        if (!settings.IsActive) return;
        // Time-based risk metrics must reflect execution time, not the older triggering event.
        context = context with { TriggerEvent = context.TriggerEvent with { OccurredAt = DateTime.UtcNow } };
        var result = await riskAgent.AnalyzeAsync(context, cancellationToken);
        await resultStore.SaveRiskAnalysisAsync(context, result, cancellationToken);
    }
}
