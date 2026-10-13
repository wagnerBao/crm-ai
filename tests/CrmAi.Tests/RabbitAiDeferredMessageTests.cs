using System.Reflection;
using System.Text;
using CrmAi.Application;
using CrmAi.Domain;
using CrmAi.Infrastructure.RabbitMq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CrmAi.Tests;

public sealed class RabbitAiDeferredMessageTests
{
    [Fact]
    public void Deferred_publication_preserves_the_batch_and_identity_and_uses_confirmed_direct_routing()
    {
        var calls = new List<string>();
        byte[]? published = null;
        IBasicProperties? properties = null;
        var channel = Proxy<IModel>((method, args) =>
        {
            calls.Add(method.Name);
            if (method.Name == nameof(IModel.CreateBasicProperties)) return Properties();
            if (method.Name == nameof(IModel.BasicPublish))
            {
                Assert.Equal("", args![0]);
                Assert.StartsWith("original.queue.ai-deferred.", (string)args[1]!);
                Assert.True((bool)args[2]!);
                properties = (IBasicProperties)args[3]!;
                published = ((ReadOnlyMemory<byte>)args[4]!).ToArray();
            }
            return Default(method.ReturnType);
        });
        var delivery = Delivery("{\"opportunityId\":\"opportunity-1\",\"data\":{\"text\":\"synthetic batch\"}}");
        delivery.BasicProperties.CorrelationId = "synthetic-correlation";
        delivery.BasicProperties.Headers = new Dictionary<string, object> { ["custom-header"] = "preserved" };

        RabbitMqAiDeferredMessages.PublishConfirmed(channel, "original.queue", delivery,
            new AiInvocationDeferredException(DateTime.UtcNow.AddHours(1), "provider_quota_exhausted"),
            "resolved-event-id", "opportunity.whatsapp.conversation.batch");

        Assert.Equal(delivery.Body.ToArray(), published);
        Assert.True(properties!.Persistent);
        Assert.Equal("synthetic-correlation", properties.CorrelationId);
        Assert.Equal("resolved-event-id", properties.MessageId);
        Assert.Equal("opportunity.whatsapp.conversation.batch", properties.Type);
        Assert.Equal("preserved", properties.Headers["custom-header"]);
        Assert.True(calls.IndexOf(nameof(IModel.BasicPublish)) < calls.IndexOf(nameof(IModel.WaitForConfirmsOrDie)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Original_delivery_is_acknowledged_only_after_confirm_and_state_update(bool loseConfirm, bool failStateUpdate)
    {
        var calls = new List<string>();
        var publisher = Proxy<IModel>((method, args) =>
        {
            calls.Add("publisher." + method.Name);
            if (method.Name == nameof(IModel.CreateBasicProperties)) return Properties();
            if (method.Name == nameof(IModel.WaitForConfirmsOrDie) && loseConfirm) throw new TimeoutException("Synthetic lost confirm");
            return Default(method.ReturnType);
        });
        var consumerChannel = Proxy<IModel>((method, args) =>
        {
            calls.Add("consumer." + method.Name);
            if (method.Name == "get_IsOpen") return true;
            return Default(method.ReturnType);
        });
        var connection = Proxy<IConnection>((method, args) =>
            method.Name == nameof(IConnection.CreateModel) ? publisher : Default(method.ReturnType));
        await using var provider = new ServiceCollection().BuildServiceProvider();
        using var consumer = new DeferredConsumer(provider.GetRequiredService<IServiceScopeFactory>(), calls, failStateUpdate);
        SetField(consumer, "_channel", consumerChannel);
        SetField(consumer, "_connection", connection);
        var delivery = Delivery("{\"eventId\":\"synthetic-event\",\"type\":\"opportunity.whatsapp.conversation.batch\",\"opportunityId\":\"opportunity-1\"}");

        await (Task)typeof(RabbitMqOpportunityEventConsumerBase).GetMethod("HandleMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(consumer, [delivery, new RabbitMqOptions(), CancellationToken.None])!;

        if (loseConfirm || failStateUpdate)
        {
            Assert.DoesNotContain("consumer.BasicAck", calls);
            Assert.Contains("consumer.BasicNack", calls);
        }
        else
        {
            Assert.True(calls.IndexOf("publisher.WaitForConfirmsOrDie") < calls.IndexOf("consumer.BasicAck"));
            Assert.DoesNotContain("consumer.BasicNack", calls);
        }
        if (loseConfirm) Assert.DoesNotContain("state.MarkDeferred", calls);
        else
        {
            Assert.True(calls.IndexOf("publisher.WaitForConfirmsOrDie") < calls.IndexOf("state.MarkDeferred"));
            if (!failStateUpdate) Assert.True(calls.IndexOf("state.MarkDeferred") < calls.IndexOf("consumer.BasicAck"));
        }
        Assert.Contains("publisher.QueueDeclare", calls); // Recreates deleted delay queues before republishing.
    }

    [Fact]
    public void Delay_queues_return_to_the_original_consumer_without_fanout_and_retain_work_when_destination_is_unavailable()
    {
        var declared = new List<Dictionary<string, object>>();
        var channel = Proxy<IModel>((method, args) =>
        {
            if (method.Name == nameof(IModel.QueueDeclare)) declared.Add((Dictionary<string, object>)args![4]!);
            return Default(method.ReturnType);
        });

        RabbitMqAiDeferredMessages.DeclareTopology(channel, "original.queue");

        Assert.NotEmpty(declared);
        Assert.All(declared, arguments =>
        {
            Assert.Equal("quorum", arguments["x-queue-type"]);
            Assert.Equal("", arguments["x-dead-letter-exchange"]);
            Assert.Equal("original.queue", arguments["x-dead-letter-routing-key"]);
            Assert.Equal("at-least-once", arguments["x-dead-letter-strategy"]);
            Assert.Equal("reject-publish", arguments["x-overflow"]);
        });
    }

    [Fact]
    public void Retry_keeps_fallback_event_type_id_and_timestamp_when_original_json_omits_them()
    {
        var original = Delivery("{\"opportunityId\":\"opportunity-1\",\"data\":{\"text\":\"synthetic batch\"}}");
        var deserialize = typeof(RabbitMqOpportunityEventConsumerBase).GetMethod("DeserializeEvent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var first = (OpportunityEvent)deserialize.Invoke(null, [original])!;
        IBasicProperties? properties = null;
        var publisher = Proxy<IModel>((method, args) =>
        {
            if (method.Name == nameof(IModel.CreateBasicProperties)) return Properties();
            if (method.Name == nameof(IModel.BasicPublish)) properties = (IBasicProperties)args![3]!;
            return Default(method.ReturnType);
        });
        RabbitMqAiDeferredMessages.PublishConfirmed(publisher, "original.queue", original,
            new AiInvocationDeferredException(DateTime.UtcNow.AddHours(1), "provider_quota_exhausted"),
            first.EventId, first.Type, first.OccurredAt);
        var redelivery = new BasicDeliverEventArgs { Exchange = "", Body = original.Body, BasicProperties = properties };
        var second = (OpportunityEvent)deserialize.Invoke(null, [redelivery])!;

        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.OccurredAt, second.OccurredAt);
        Assert.Equal(first.Data["text"], second.Data["text"]);
    }

    private static BasicDeliverEventArgs Delivery(string body) => new()
    {
        DeliveryTag = 42,
        Exchange = "crm.events.opportunity.whatsapp.conversation.batch",
        BasicProperties = Properties(),
        Body = Encoding.UTF8.GetBytes(body)
    };
    private static void SetField(object target, string name, object value) =>
        typeof(RabbitMqOpportunityEventConsumerBase).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, MethodProxy>();
        ((MethodProxy)(object)proxy).Handler = invoke;
        return proxy;
    }
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static IBasicProperties Properties()
    {
        var values = new Dictionary<string, object?>();
        return Proxy<IBasicProperties>((method, args) =>
        {
            if (method.Name.StartsWith("set_")) { values[method.Name[4..]] = args![0]; return null; }
            if (method.Name.StartsWith("get_")) return values.GetValueOrDefault(method.Name[4..]) ?? Default(method.ReturnType);
            return Default(method.ReturnType);
        });
    }

    public class MethodProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
    private sealed class DeferredConsumer(IServiceScopeFactory scopes, List<string>? calls = null, bool failStateUpdate = false)
        : RabbitMqOpportunityEventConsumerBase(scopes, Options.Create(new RabbitMqOptions()), NullLogger.Instance)
    {
        protected override string ConnectionName => "synthetic";
        protected override string QueueName(RabbitMqOptions options) => "original.queue";
        protected override IReadOnlyCollection<string> ExchangeNames(RabbitMqOptions options) => [];
        protected override Task ProcessAsync(IServiceProvider services, OpportunityEvent opportunityEvent, CancellationToken cancellationToken) =>
            Task.FromException(new AiInvocationDeferredException(DateTime.UtcNow.AddHours(1), "provider_quota_exhausted"));
        protected override Task OnAiDeferredAsync(IServiceProvider services, OpportunityEvent opportunityEvent,
            AiInvocationDeferredException deferred, CancellationToken cancellationToken)
        {
            calls?.Add("state.MarkDeferred");
            return failStateUpdate ? Task.FromException(new IOException("Synthetic database failure")) : Task.CompletedTask;
        }
    }
}
