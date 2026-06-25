using CrmAi.Application;
using CrmAi.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);

        return Results.Ok(new { status = "healthy" });
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/", () => Results.Ok(new
{
    service = "crm-ai",
    status = "running",
    queues = new
    {
        opportunityAnalysis = builder.Configuration["RabbitMQ:OpportunityAnalysisQueue"] ?? "crm.projections.opportunity-analysis",
        dailyCheckin = builder.Configuration["RabbitMQ:DailyCheckinQueue"] ?? "crm.projections.daily-checkin",
        dailyCheckout = builder.Configuration["RabbitMQ:DailyCheckoutQueue"] ?? "crm.projections.daily-checkout",
        gamification = builder.Configuration["RabbitMQ:GamificationQueue"] ?? "crm.projections.gamification"
    }
}));

app.Run();
