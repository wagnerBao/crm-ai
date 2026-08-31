using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrmAi.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OpenAiRiskAnalysisOptions>(configuration.GetSection(OpenAiRiskAnalysisOptions.SectionName));
        services.AddScoped<IOpportunityAnalysisEventProcessor, OpportunityAnalysisEventProcessor>();
        services.AddScoped<IActivityAnalysisEventProcessor, ActivityAnalysisEventProcessor>();
        services.AddSingleton<CommercialRuleAssessmentService>();
        services.AddSingleton<RiskAnalysisAgentInputBuilder>();
        services.AddHttpClient<IOpenAiRiskAnalysisClient, OpenAiResponsesRiskAnalysisClient>();
        services.AddHttpClient<IOpenAiWhatsappConversationAnalysisClient, OpenAiResponsesWhatsappConversationAnalysisClient>();
        services.AddHttpClient<IOpenAiMeetingAudioClient, OpenAiMeetingAudioClient>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddHttpClient<IOpenAiDailyCheckoutClient, OpenAiResponsesDailyCheckoutClient>();
        services.AddHttpClient<IOpenAiSuggestionQualityAuditClient, OpenAiSuggestionQualityAuditClient>();
        services.AddHttpClient<IOpenAiSuggestionCompletionVerificationClient, OpenAiSuggestionCompletionVerificationClient>();
        services.AddScoped<IRiskAnalysisAgent, RiskAnalysisAgent>();
        services.AddScoped<IWhatsappConversationAnalysisAgent, WhatsappConversationAnalysisAgent>();

        return services;
    }
}
