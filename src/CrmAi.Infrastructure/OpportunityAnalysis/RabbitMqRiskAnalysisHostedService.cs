using System.Text.Json;
using CrmAi.Application;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrmAi.Infrastructure.OpportunityAnalysis;

public sealed class RabbitMqRiskAnalysisHostedService(IServiceScopeFactory scopeFactory,
    RabbitMqRiskAnalysisMessages messages, IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqRiskAnalysisHostedService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IRiskAnalysisState>().InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedulers = Math.Clamp(options.Value.RiskAnalysisSchedulerCount,0,32);
        var executors = Math.Clamp(options.Value.RiskAnalysisConsumerCount,0,32);
        if (schedulers+executors==0) throw new InvalidOperationException("At least one risk scheduler or executor must be enabled.");
        return Task.WhenAll(Enumerable.Range(0,schedulers).Select(i => ConsumeAsync(true,i,stoppingToken))
            .Concat(Enumerable.Range(0,executors).Select(i => ConsumeAsync(false,i,stoppingToken))));
    }
    private async Task ConsumeAsync(bool scheduling, int worker, CancellationToken stoppingToken)
    {
        var queue = scheduling ? messages.ScheduleQueue : messages.ReadyQueue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connectionToken = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var deliveryToken = connectionToken.Token;
                using var connection = messages.CreateFactory(true).CreateConnection($"crm-ai-risk-{(scheduling ? "scheduler" : "executor")}-{worker}");
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdown += (_,_) => disconnected.TrySetResult();
                using var channel = connection.CreateModel();
                messages.DeclareTopology(channel);
                channel.BasicQos(0,1,false); // Parallelism is explicit workers, never unbounded callbacks.
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_,args) =>
                {
                    try
                    {
                        await HandleAsync(scheduling,args.Body.ToArray(),deliveryToken);
                        channel.BasicAck(args.DeliveryTag,false);
                    }
                    catch (OperationCanceledException) when (deliveryToken.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        logger.LogError(exception,"Risk delivery failed on {Queue}. It remains in RabbitMQ.",queue);
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5),deliveryToken);
                            if (channel.IsOpen) channel.BasicNack(args.DeliveryTag,false,true);
                        }
                        catch (OperationCanceledException) when (deliveryToken.IsCancellationRequested) { }
                    }
                };
                channel.BasicConsume(queue,false,consumer);
                logger.LogInformation("Risk consumer {Queue}/{Worker} started.",queue,worker);
                try { await disconnected.Task.WaitAsync(stoppingToken); }
                finally { connectionToken.Cancel(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception,"Risk consumer {Queue} will reconnect.",queue); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task HandleAsync(bool scheduling, byte[] body, CancellationToken ct)
    {
        RiskAnalysisRequest? request = null;
        RiskAnalysisWork? work = null;
        try
        {
            if (scheduling)
            {
                request=JsonSerializer.Deserialize<RiskAnalysisRequest>(body);
                if (request is null || request.CompanyId==Guid.Empty || request.OpportunityId==Guid.Empty
                    || request.Event is null || string.IsNullOrWhiteSpace(request.Event.EventId)
                    || request.Event.OpportunityId!=request.OpportunityId.ToString()) throw new JsonException("Invalid risk schedule envelope.");
            }
            else
            {
                work=JsonSerializer.Deserialize<RiskAnalysisWork>(body);
                if (work is null || work.CompanyId==Guid.Empty || work.OpportunityId==Guid.Empty || work.Revision<1
                    || work.Event is null || work.Event.OpportunityId!=work.OpportunityId.ToString()) throw new JsonException("Invalid risk work envelope.");
            }
        }
        catch (JsonException exception) { messages.PublishInvalid(body,exception.Message); return; }
        await using var scope=scopeFactory.CreateAsyncScope();
        var handler=scope.ServiceProvider.GetRequiredService<RiskAnalysisMessageHandler>();
        if (scheduling) await handler.ScheduleAsync(request!,ct);
        else await handler.ExecuteAsync(work!,ct);
    }
}
