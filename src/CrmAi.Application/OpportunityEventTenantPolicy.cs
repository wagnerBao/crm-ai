using CrmAi.Domain;

namespace CrmAi.Application;

public static class OpportunityEventTenantPolicy
{
    // Legacy producers omit CompanyId. The stored opportunity remains the source
    // of tenant identity; supplied envelope/payload hints must agree before children load.
    public static bool Matches(OpportunityEvent trigger, OpportunitySnapshot opportunity)
    {
        if (!Guid.TryParse(trigger.OpportunityId, out var eventOpportunity)
            || !Guid.TryParse(opportunity.Id, out var storedOpportunity)
            || eventOpportunity != storedOpportunity) return false;
        return MatchesCompany(trigger, opportunity.CompanyId);
    }

    public static bool MatchesCompany(OpportunityEvent trigger, string? companyId)
    {
        var hints = new List<string?> { trigger.CompanyId };
        if (trigger.Data.TryGetValue("companyId", out var payloadCompany)) hints.Add(payloadCompany?.ToString());
        foreach (var hint in hints)
        {
            if (hint is null) continue;
            if (!Guid.TryParse(hint, out var claimed)
                || !Guid.TryParse(companyId, out var actual)
                || claimed != actual) return false;
        }
        return true;
    }
}
