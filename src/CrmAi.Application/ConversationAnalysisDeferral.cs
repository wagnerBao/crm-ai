using CrmAi.Domain;

namespace CrmAi.Application;

public interface IConversationAnalysisDeferralStore
{
    Task MarkDeferredAsync(OpportunityEvent opportunityEvent, string reason, CancellationToken cancellationToken);
}
