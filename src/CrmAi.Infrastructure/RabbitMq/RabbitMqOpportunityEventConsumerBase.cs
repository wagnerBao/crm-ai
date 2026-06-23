using System.Text;
using System.Text.Json;
using CrmAi.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrmAi.Infrastructure.RabbitMq;

public abstract class RabbitMqOpportunityEventConsumerBase(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger logger) : BackgroundService
{
    private IConnection? _connection;
    private IModel? _channel;

    protected abstract string ConnectionName { get; }
    protected abstract string QueueName(RabbitMqOptions options);
    protected abstract IReadOnlyCollection<string> ExchangeNames(RabbitMqOptions options);
    protected abstract Task ProcessAsync(IServiceProvider services, OpportunityEvent opportunityEvent, CancellationToken cancellationToken);
    protected virtual bool RequiresOpportunityId => true;

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

        var queueName = QueueName(rabbitOptions);
        var exchangeNames = ExchangeNames(rabbitOptions);

        _connection = factory.CreateConnection(ConnectionName);
        _channel = _connection.CreateModel();

        foreach (var exchange in exchangeNames)
        {
            _channel.ExchangeDeclare(exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        }

        _channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false);
        foreach (var exchange in exchangeNames)
        {
            _channel.QueueBind(queueName, exchange, routingKey: string.Empty);
        }

        _channel.BasicQos(prefetchSize: 0, prefetchCount: rabbitOptions.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, args) => await HandleMessageAsync(args, rabbitOptions, stoppingToken);

        _channel.BasicConsume(queueName, autoAck: false, consumer);

        logger.LogInformation(
            "RabbitMQ consumer {ConsumerName} started on queue {QueueName} bound to {ExchangeCount} exchanges.",
            GetType().Name,
            queueName,
            exchangeNames.Count);

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
            var opportunityEvent = DeserializeEvent(args);
            if (RequiresOpportunityId && string.IsNullOrWhiteSpace(opportunityEvent.OpportunityId))
            {
                logger.LogWarning("Discarding RabbitMQ event without opportunityId from exchange {Exchange}.", args.Exchange);
                _channel.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            await ProcessAsync(scope.ServiceProvider, opportunityEvent, cancellationToken);

            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to process RabbitMQ message from exchange {Exchange} in {ConsumerName}.", args.Exchange, GetType().Name);
            _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: rabbitOptions.RequeueOnFailure);
        }
    }

    private static OpportunityEvent DeserializeEvent(BasicDeliverEventArgs args)
    {
        var json = Encoding.UTF8.GetString(args.Body.Span);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var eventId = GetString(root, "eventId")
            ?? args.BasicProperties?.MessageId
            ?? Guid.NewGuid().ToString();
        var type = GetString(root, "type")
            ?? args.BasicProperties?.Type
            ?? args.Exchange;
        var occurredAt = GetDateTime(root, "occurredAt") ?? DateTime.UtcNow;
        var opportunityId = GetString(root, "opportunityId") ?? string.Empty;
        var userId = GetString(root, "userId");
        var data = ReadData(root);

        return new OpportunityEvent(eventId, type, occurredAt, opportunityId, userId, data);
    }

    private static Dictionary<string, object?> ReadData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return dataElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ReadJsonValue(property.Value));
    }

    private static object? ReadJsonValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? GetDateTime(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var dateTime)
            ? dateTime
            : null;
}
