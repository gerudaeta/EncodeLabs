using RabbitMQ.Client;

namespace ChatInbox.Infrastructure.Messaging;

public static class RabbitTopology
{
    public const string InboundExchange = "chat-inbox.inbound";
    public const string InboundQueue = "chat-inbox.inbound.q";
    public const string InboundRoutingKey = "chat-inbox.inbound";
    public const string DeadExchange = "chat-inbox.dead";
    public const string DeadQueue = "chat-inbox.dead.q";
    public const string DeadRoutingKey = "chat-inbox.dead";

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(DeadExchange, type: "direct", durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, DeadRoutingKey,
            cancellationToken: cancellationToken);

        var arguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = 5,
            ["x-delayed-retry-type"] = "all",
            ["x-delayed-retry-min"] = 1000,
            ["x-delayed-retry-max"] = 30000,
            ["x-dead-letter-exchange"] = DeadExchange,
            ["x-dead-letter-routing-key"] = DeadRoutingKey
        };
        await channel.ExchangeDeclareAsync(InboundExchange, type: "direct", durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(InboundQueue, durable: true, exclusive: false,
            autoDelete: false, arguments: arguments, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(InboundQueue, InboundExchange, InboundRoutingKey,
            cancellationToken: cancellationToken);
    }
}
