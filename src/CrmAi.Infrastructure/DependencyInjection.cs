using CrmAi.Application;
using CrmAi.Infrastructure.DailyCheckouts;
using CrmAi.Infrastructure.DailyCheckins;
using CrmAi.Infrastructure.Gamification;
using CrmAi.Infrastructure.OpportunityAnalysis;
using CrmAi.Infrastructure.Persistence;
using CrmAi.Infrastructure.RabbitMq;
using CrmAi.Infrastructure.SkoposCoach;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CrmAi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));

        var connectionString = configuration.GetConnectionString("CrmDatabase")
            ?? throw new InvalidOperationException("Connection string 'CrmDatabase' was not configured.");

        services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddScoped<IOpportunityContextRepository, PostgresOpportunityContextRepository>();
        services.AddScoped<IAiAgentRuntimeSettingsRepository, PostgresAiAgentRuntimeSettingsRepository>();
        services.AddScoped<IAiAgentInvocationLogStore, PostgresAiAgentInvocationLogStore>();
        services.AddScoped<IAnalysisResultStore, PostgresAnalysisResultStore>();
        services.AddScoped<IWhatsappConversationActionStore, PostgresWhatsappConversationActionStore>();
        services.AddScoped<IWhatsappSuggestionContextRepository, PostgresWhatsappSuggestionContextRepository>();
        services.AddScoped<IWhatsappConversationAnalysisScheduler, PostgresWhatsappConversationAnalysisScheduler>();
        services.AddScoped<IMeetingAudioAnalysisService, PostgresMeetingAudioAnalysisService>();
        services.AddScoped<IDailyCheckinProjectionService, PostgresDailyCheckinProjectionService>();
        services.AddScoped<IDailyCheckoutSnapshotService, PostgresDailyCheckoutSnapshotService>();
        services.AddScoped<IGamificationProjectionService, PostgresGamificationProjectionService>();
        services.AddScoped<SuggestionQualityAuditProcessor>();
        services.AddScoped<SkoposCoachProjectionService>();
        services.AddHttpClient<SkoposCoachSynthesisClient>();
        services.AddHostedService<RabbitMqOpportunityAnalysisConsumer>();
        services.AddHostedService<RabbitMqActivityAnalysisConsumer>();
        services.AddHostedService<WhatsappConversationAnalysisHostedService>();
        services.AddHostedService<RabbitMqDailyCheckinConsumer>();
        services.AddHostedService<RabbitMqDailyCheckoutRunConsumer>();
        services.AddHostedService<RabbitMqGamificationConsumer>();
        services.AddHostedService<DailyCheckinSnapshotHostedService>();
        services.AddHostedService<DailyCheckoutSnapshotHostedService>();
        services.AddHostedService<SuggestionQualityAuditHostedService>();
        services.AddHostedService<SkoposCoachHostedService>();

        return services;
    }
}
