using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrmAi.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenAiRiskAnalysisOptions>(configuration.GetSection(OpenAiRiskAnalysisOptions.SectionName));
        services.AddScoped<IOpportunityAnalysisEventProcessor, OpportunityAnalysisEventProcessor>();
        services.AddSingleton<CommercialRuleAssessmentService>();
        services.AddSingleton<RiskAnalysisAgentInputBuilder>();
        services.AddHttpClient<IOpenAiRiskAnalysisClient, OpenAiResponsesRiskAnalysisClient>();
        services.AddScoped<IRiskAnalysisAgent, RiskAnalysisAgent>();

        return services;
    }
}
