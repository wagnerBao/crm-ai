using CrmAi.Application;
using CrmAi.Infrastructure.DailyCheckins;
using CrmAi.Infrastructure.Gamification;
using CrmAi.Infrastructure.OpportunityAnalysis;
using CrmAi.Infrastructure.Persistence;
using CrmAi.Infrastructure.RabbitMq;
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
        services.AddScoped<IAnalysisResultStore, PostgresAnalysisResultStore>();
        services.AddScoped<IDailyCheckinProjectionService, PostgresDailyCheckinProjectionService>();
        services.AddScoped<IGamificationProjectionService, PostgresGamificationProjectionService>();
        services.AddHostedService<RabbitMqOpportunityAnalysisConsumer>();
        services.AddHostedService<RabbitMqDailyCheckinConsumer>();
        services.AddHostedService<RabbitMqGamificationConsumer>();
        services.AddHostedService<DailyCheckinSnapshotHostedService>();

        return services;
    }
}
