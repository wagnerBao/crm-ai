namespace CrmAi.Domain;

public sealed record DailyCheckoutSettingsSnapshot(
    string? CompanyId,
    string RunAt,
    string TimeZoneId,
    bool ConsiderPreviousDayWhenRunBeforeNoon);

public sealed record DailyCheckoutAnalysisInput(
    DateOnly Date,
    DailyCheckoutSettingsSnapshot Settings,
    object Totals,
    IReadOnlyCollection<object> Metrics,
    object Charts,
    object Tables,
    IReadOnlyCollection<object> UpdatedOpportunities,
    IReadOnlyCollection<object> RiskItems,
    IReadOnlyCollection<object> LowEffectiveness);

public sealed record OpenAiDailyCheckoutResponse(
    DailyCheckoutExecutiveSummaryResponse ExecutiveSummary,
    IReadOnlyCollection<DailyCheckoutTextItemResponse> Alerts,
    IReadOnlyCollection<DailyCheckoutRecommendationResponse> Recommendations);

public sealed record DailyCheckoutExecutiveSummaryResponse(string Headline, string Focus);

public sealed record DailyCheckoutTextItemResponse(string Title, string Description, string Severity);

public sealed record DailyCheckoutRecommendationResponse(string Title, string Description, string Priority);

