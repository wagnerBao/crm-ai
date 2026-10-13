using CrmAi.Application;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrmAi.Infrastructure.RabbitMq;

internal static class RabbitMqAiDeferredMessages
{
    internal static string DelayQueue(string queue, int seconds) => $"{queue}.ai-deferred.{seconds}s";

    internal static void DeclareTopology(IModel channel, string readyQueue)
    {
        foreach (var seconds in RabbitMqRiskAnalysisMessages.DelaySeconds)
        {
            channel.QueueDeclare(DelayQueue(readyQueue, seconds), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object>
                {
                    ["x-queue-type"] = "quorum",
                    ["x-message-ttl"] = seconds * 1000,
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = readyQueue,
                    ["x-dead-letter-strategy"] = "at-least-once",
                    ["x-overflow"] = "reject-publish"
                });
        }
    }

    // The caller acknowledges the original delivery only after this method returns successfully.
    internal static void PublishConfirmed(IModel channel, string readyQueue, BasicDeliverEventArgs delivery,
        AiInvocationDeferredException deferred, string? eventId = null, string? eventType = null, DateTime? occurredAt = null)
    {
        var queue = DelayQueue(readyQueue, RabbitMqRiskAnalysisMessages.SelectDelay(deferred.RetryAtUtc - DateTime.UtcNow));
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = delivery.BasicProperties?.ContentType ?? "application/json";
        properties.ContentEncoding = delivery.BasicProperties?.ContentEncoding;
        properties.CorrelationId = delivery.BasicProperties?.CorrelationId;
        properties.MessageId = eventId ?? delivery.BasicProperties?.MessageId ?? Guid.NewGuid().ToString();
        properties.Type = eventType ?? delivery.BasicProperties?.Type ?? delivery.Exchange;
        properties.AppId = delivery.BasicProperties?.AppId;
        properties.Headers = delivery.BasicProperties?.Headers is { } headers
            ? new Dictionary<string, object>(headers)
            : new Dictionary<string, object>();
        properties.Headers["x-ai-deferred-reason"] = deferred.Reason;
        properties.Headers["x-ai-retry-at"] = deferred.RetryAtUtc.ToString("O");
        if (occurredAt is not null) properties.Headers["x-ai-occurred-at"] = occurredAt.Value.ToString("O");

        string? returned = null;
        void OnReturn(object? sender, BasicReturnEventArgs args) => returned = args.ReplyText;
        channel.BasicReturn += OnReturn;
        try
        {
            // Route directly to this consumer's queue, avoiding fanout to unrelated projections.
            channel.BasicPublish("", queue, mandatory: true, basicProperties: properties, body: delivery.Body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
            if (returned is not null) throw new IOException("Deferred AI message could not be routed: " + returned);
        }
        finally { channel.BasicReturn -= OnReturn; }
    }
}
