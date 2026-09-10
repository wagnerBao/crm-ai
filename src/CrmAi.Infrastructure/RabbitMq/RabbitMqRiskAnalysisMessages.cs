using System.Text.Json;
using CrmAi.Application;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CrmAi.Infrastructure.RabbitMq;

public sealed class RabbitMqRiskAnalysisMessages(IOptions<RabbitMqOptions> options) : IRiskAnalysisMessages, IDisposable
{
    // Fixed TTL per queue avoids a long-delay message blocking shorter delays behind it.
    internal static readonly int[] DelaySeconds = [1, 10, 60, 300, 900, 1800, 3600, 21600, 86400];
    private readonly object _gate = new();
    private IConnection? _connection;
    private IModel? _channel;
    public string ReadyQueue => options.Value.RiskAnalysisQueue;
    public string ScheduleQueue => ReadyQueue + ".schedule";
    public string FailureQueue => ReadyQueue + ".dlq";
    public string ReadyExchange => ReadyQueue + ".ready";
    public string FailureExchange => ReadyQueue + ".failures";
    public string DelayQueue(int seconds) => ReadyQueue + ".delay." + seconds + "s";

    public ConnectionFactory CreateFactory(bool dispatchAsync = false) => new()
    {
        Uri = new Uri(options.Value.Uri), DispatchConsumersAsync = dispatchAsync,
        AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
    };

    public void DeclareTopology(IModel channel)
    {
        channel.ExchangeDeclare(ReadyExchange, ExchangeType.Direct, true, false);
        channel.ExchangeDeclare(FailureExchange, ExchangeType.Direct, true, false);
        channel.QueueDeclare(FailureQueue, true, false, false, new Dictionary<string,object> { ["x-queue-type"]="quorum" });
        channel.QueueBind(FailureQueue, FailureExchange, "");
        channel.QueueDeclare(ScheduleQueue, true, false, false, QueueArguments(FailureExchange));
        channel.QueueDeclare(ReadyQueue, true, false, false, QueueArguments(FailureExchange));
        channel.QueueBind(ReadyQueue, ReadyExchange, "");
        foreach (var seconds in DelaySeconds)
        {
            var arguments = QueueArguments(ReadyExchange);
            arguments["x-message-ttl"] = seconds * 1000;
            channel.QueueDeclare(DelayQueue(seconds), true, false, false, arguments);
        }
    }
    private static Dictionary<string,object> QueueArguments(string deadLetterExchange) => new()
    {
        ["x-queue-type"]="quorum", ["x-dead-letter-exchange"]=deadLetterExchange,
        ["x-dead-letter-routing-key"]="", ["x-dead-letter-strategy"]="at-least-once",
        ["x-overflow"]="reject-publish", ["x-delivery-limit"]=1000
    };

    internal static int SelectDelay(TimeSpan remaining) =>
        DelaySeconds.LastOrDefault(seconds => seconds <= Math.Ceiling(remaining.TotalSeconds), 1);

    public void PublishRequest(RiskAnalysisRequest request) => Publish(ScheduleQueue,
        JsonSerializer.SerializeToUtf8Bytes(request), request.Event.EventId);
    public void PublishWork(RiskAnalysisWork work)
    {
        var remaining = work.NotBeforeUtc - DateTime.UtcNow;
        var queue = remaining <= TimeSpan.Zero ? ReadyQueue : DelayQueue(SelectDelay(remaining));
        Publish(queue, JsonSerializer.SerializeToUtf8Bytes(work), $"{work.CompanyId:N}:{work.OpportunityId:N}:{work.Revision}");
    }
    public void PublishFailure(RiskAnalysisWork work, string error) => Publish(FailureQueue,
        JsonSerializer.SerializeToUtf8Bytes(work), $"{work.CompanyId:N}:{work.OpportunityId:N}:{work.Revision}", error);
    public void PublishInvalid(byte[] body, string error) => Publish(FailureQueue, body, Guid.NewGuid().ToString("N"), error);

    private void Publish(string queue, byte[] body, string messageId, string? error = null)
    {
        lock (_gate)
        {
            try
            {
                if (_channel is not { IsOpen:true })
                {
                    Close();
                    _connection = CreateFactory().CreateConnection("crm-ai-risk-publisher");
                    _channel = _connection.CreateModel();
                    DeclareTopology(_channel);
                    _channel.ConfirmSelect();
                }
                string? returned = null;
                void OnReturn(object? sender, RabbitMQ.Client.Events.BasicReturnEventArgs ev) => returned = ev.ReplyText;
                _channel.BasicReturn += OnReturn;
                try
                {
                    var properties = _channel.CreateBasicProperties();
                    properties.Persistent=true; properties.ContentType="application/json"; properties.MessageId=messageId;
                    if (error is not null) properties.Headers=new Dictionary<string,object> { ["x-risk-error"]=error[..Math.Min(error.Length,2000)] };
                    _channel.BasicPublish("",queue,true,properties,body);
                    _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
                    if (returned is not null) throw new IOException("Risk message was not routed: " + returned);
                }
                finally { _channel.BasicReturn -= OnReturn; }
            }
            catch { Close(); throw; }
        }
    }
    private void Close() { _channel?.Dispose(); _channel=null; _connection?.Dispose(); _connection=null; }
    public void Dispose() { lock (_gate) Close(); }
}
