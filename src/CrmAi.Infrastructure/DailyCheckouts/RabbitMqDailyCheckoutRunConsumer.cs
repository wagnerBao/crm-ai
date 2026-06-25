using System.Text;
using System.Text.Json;
using CrmAi.Application;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrmAi.Infrastructure.DailyCheckouts;

public sealed record DailyCheckoutRunMessage(
    string? EventId,
    string? Type,
    string? CompanyId,
    string? UserId,
    DateOnly? Date,
    DateTime? CreatedAt);

public sealed class RabbitMqDailyCheckoutRunConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqDailyCheckoutRunConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private IConnection? _connection;
    private IModel? _channel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rabbitOptions = options.Value;
        var factory = new ConnectionFactory
        {
            Uri = new Uri(rabbitOptions.Uri),
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
        };

        _connection = factory.CreateConnection("crm-ai-daily-checkout");
        _channel = _connection.CreateModel();

        DeclareDeadLetterTopology(_channel, rabbitOptions);
        _channel.QueueDeclare(rabbitOptions.DailyCheckoutQueue, durable: true, exclusive: false, autoDelete: false);
        _channel.BasicQos(prefetchSize: 0, prefetchCount: rabbitOptions.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, args) => await HandleMessageAsync(args, rabbitOptions, stoppingToken);

        _channel.BasicConsume(rabbitOptions.DailyCheckoutQueue, autoAck: false, consumer);
        logger.LogInformation("RabbitMQ daily checkout consumer started on queue {QueueName}.", rabbitOptions.DailyCheckoutQueue);

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }

    private async Task HandleMessageAsync(BasicDeliverEventArgs args, RabbitMqOptions rabbitOptions, CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var message = Deserialize(args);
            if (string.IsNullOrWhiteSpace(message.CompanyId))
            {
                logger.LogWarning("Discarding daily checkout request without companyId.");
                PublishToDeadLetterQueue(args, rabbitOptions, "MissingCompanyId", "Daily checkout request did not include companyId.");
                _channel.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDailyCheckoutSnapshotService>();
            await service.GenerateSnapshotAsync(message.CompanyId, message.Date, cancellationToken);

            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to process daily checkout RabbitMQ message.");
            if (rabbitOptions.RequeueOnFailure && !args.Redelivered)
            {
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            PublishToDeadLetterQueue(args, rabbitOptions, exception.GetType().Name, exception.Message);
            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
    }

    private static DailyCheckoutRunMessage Deserialize(BasicDeliverEventArgs args)
    {
        var json = Encoding.UTF8.GetString(args.Body.Span);
        return JsonSerializer.Deserialize<DailyCheckoutRunMessage>(json, SerializerOptions)
            ?? new DailyCheckoutRunMessage(args.BasicProperties?.MessageId, args.BasicProperties?.Type, null, null, null, DateTime.UtcNow);
    }

    private static void DeclareDeadLetterTopology(IModel channel, RabbitMqOptions rabbitOptions)
    {
        var deadLetterQueueName = DeadLetterQueueName(rabbitOptions);
        channel.ExchangeDeclare(rabbitOptions.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(deadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(deadLetterQueueName, rabbitOptions.DeadLetterExchange, routingKey: rabbitOptions.DailyCheckoutQueue);
    }

    private void PublishToDeadLetterQueue(BasicDeliverEventArgs args, RabbitMqOptions rabbitOptions, string errorType, string errorMessage)
    {
        if (_channel is null)
        {
            return;
        }

        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = args.BasicProperties?.ContentType ?? "application/json";
        properties.MessageId = args.BasicProperties?.MessageId ?? Guid.NewGuid().ToString();
        properties.Type = args.BasicProperties?.Type;
        properties.Headers = new Dictionary<string, object>
        {
            ["x-original-routing-key"] = args.RoutingKey,
            ["x-original-consumer"] = nameof(RabbitMqDailyCheckoutRunConsumer),
            ["x-error-type"] = errorType,
            ["x-error-message"] = Truncate(errorMessage, 4096),
            ["x-failed-at"] = DateTime.UtcNow.ToString("O"),
            ["x-redelivered"] = args.Redelivered
        };

        _channel.BasicPublish(
            rabbitOptions.DeadLetterExchange,
            routingKey: rabbitOptions.DailyCheckoutQueue,
            basicProperties: properties,
            body: args.Body);
    }

    private static string DeadLetterQueueName(RabbitMqOptions rabbitOptions) =>
        $"{rabbitOptions.DailyCheckoutQueue}{rabbitOptions.DeadLetterQueueSuffix}";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
