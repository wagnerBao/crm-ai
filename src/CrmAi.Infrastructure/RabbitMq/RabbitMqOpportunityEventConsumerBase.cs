using System.Text;
using System.Text.Json;
using CrmAi.Application;
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
    protected virtual bool CanProcessWithoutOpportunityId(OpportunityEvent opportunityEvent) => !RequiresOpportunityId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rabbitOptions = options.Value;
        var factory = new ConnectionFactory
        {
            Uri = new Uri(rabbitOptions.Uri),
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                StartConsumer(factory, rabbitOptions, stoppingToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                DisposeRabbitResources();
                logger.LogWarning(
                    exception,
                    "RabbitMQ consumer {ConsumerName} could not start. Retrying in 10 seconds.",
                    GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private void StartConsumer(ConnectionFactory factory, RabbitMqOptions rabbitOptions, CancellationToken stoppingToken)
    {
        var queueName = QueueName(rabbitOptions);
        var exchangeNames = ExchangeNames(rabbitOptions);

        _connection = factory.CreateConnection(ConnectionName);
        _channel = _connection.CreateModel();

        foreach (var exchange in exchangeNames)
        {
            _channel.ExchangeDeclare(exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        }

        DeclareDeadLetterTopology(_channel, rabbitOptions, queueName);
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

    }

    public override void Dispose()
    {
        DisposeRabbitResources();
        base.Dispose();
    }

    private void DisposeRabbitResources()
    {
        _channel?.Dispose();
        _channel = null;
        _connection?.Dispose();
        _connection = null;
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
            if (string.IsNullOrWhiteSpace(opportunityEvent.OpportunityId) && !CanProcessWithoutOpportunityId(opportunityEvent))
            {
                logger.LogWarning("Discarding RabbitMQ event without opportunityId from exchange {Exchange}.", args.Exchange);
                PublishToDeadLetterQueue(args, rabbitOptions, "MissingOpportunityId", "RabbitMQ event did not include opportunityId.");
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
            if (ShouldRequeue(exception, rabbitOptions, args.Redelivered))
            {
                _channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            logger.LogWarning(
                "Dead-lettering RabbitMQ message from exchange {Exchange} in {ConsumerName} after non-retryable failure.",
                args.Exchange,
                GetType().Name);
            PublishToDeadLetterQueue(args, rabbitOptions, exception.GetType().Name, exception.Message);
            _channel.BasicAck(args.DeliveryTag, multiple: false);
        }
    }

    private static void DeclareDeadLetterTopology(IModel channel, RabbitMqOptions rabbitOptions, string queueName)
    {
        var deadLetterQueueName = DeadLetterQueueName(rabbitOptions, queueName);
        channel.ExchangeDeclare(rabbitOptions.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(deadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(deadLetterQueueName, rabbitOptions.DeadLetterExchange, routingKey: queueName);
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
        properties.ContentEncoding = args.BasicProperties?.ContentEncoding;
        properties.CorrelationId = args.BasicProperties?.CorrelationId;
        properties.MessageId = args.BasicProperties?.MessageId ?? Guid.NewGuid().ToString();
        properties.Type = args.BasicProperties?.Type;
        properties.AppId = args.BasicProperties?.AppId;
        properties.Headers = new Dictionary<string, object>
        {
            ["x-original-exchange"] = args.Exchange,
            ["x-original-routing-key"] = args.RoutingKey,
            ["x-original-consumer"] = GetType().Name,
            ["x-error-type"] = errorType,
            ["x-error-message"] = Truncate(errorMessage, 4096),
            ["x-failed-at"] = DateTime.UtcNow.ToString("O"),
            ["x-redelivered"] = args.Redelivered
        };

        _channel.BasicPublish(
            rabbitOptions.DeadLetterExchange,
            routingKey: QueueName(rabbitOptions),
            basicProperties: properties,
            body: args.Body);
    }

    private static string DeadLetterQueueName(RabbitMqOptions rabbitOptions, string queueName) =>
        $"{queueName}{rabbitOptions.DeadLetterQueueSuffix}";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool ShouldRequeue(Exception exception, RabbitMqOptions rabbitOptions, bool redelivered)
    {
        if (!rabbitOptions.RequeueOnFailure || redelivered)
        {
            return false;
        }

        var openAiException = FindOpenAiException(exception);
        return openAiException is null || openAiException.IsTransient;
    }

    private static OpenAiRequestException? FindOpenAiException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OpenAiRequestException openAiException)
            {
                return openAiException;
            }
        }

        return null;
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
