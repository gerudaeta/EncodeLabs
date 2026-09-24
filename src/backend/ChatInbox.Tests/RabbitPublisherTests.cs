using System.Text.Json;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Tests.Integration;
using RabbitMQ.Client;
using Xunit;

namespace ChatInbox.Tests;

public sealed class RabbitPublisherTests(BrokerFixture broker) : IClassFixture<BrokerFixture>
{
    [Fact]
    public async Task TopologyUsesDurableQuorumRetryAndDeadLetterRouting()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.AmqpUri) }
            .CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareAsync(channel, CancellationToken.None);

        using var client = new HttpClient { BaseAddress = broker.ManagementUri };
        var credentials = new Uri(broker.AmqpUri).UserInfo;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(credentials))));
        using var queueResponse = await client.GetAsync("api/queues/%2F/chat-inbox.inbound.q");
        queueResponse.EnsureSuccessStatusCode();
        using var queue = JsonDocument.Parse(await queueResponse.Content.ReadAsStringAsync());
        var root = queue.RootElement;
        Assert.True(root.GetProperty("durable").GetBoolean());
        var args = root.GetProperty("arguments");
        Assert.Equal("quorum", args.GetProperty("x-queue-type").GetString());
        Assert.Equal(5, args.GetProperty("x-delivery-limit").GetInt32());
        Assert.Equal("all", args.GetProperty("x-delayed-retry-type").GetString());
        Assert.Equal(1000, args.GetProperty("x-delayed-retry-min").GetInt32());
        Assert.Equal(30000, args.GetProperty("x-delayed-retry-max").GetInt32());
        Assert.Equal("chat-inbox.dead", args.GetProperty("x-dead-letter-exchange").GetString());
        Assert.Equal("chat-inbox.dead", args.GetProperty("x-dead-letter-routing-key").GetString());

        using var bindingResponse = await client.GetAsync(
            "api/bindings/%2F/e/chat-inbox.inbound/q/chat-inbox.inbound.q");
        bindingResponse.EnsureSuccessStatusCode();
        using var bindings = JsonDocument.Parse(await bindingResponse.Content.ReadAsStringAsync());
        Assert.Contains(bindings.RootElement.EnumerateArray(), item =>
            item.GetProperty("routing_key").GetString() == RabbitTopology.InboundRoutingKey);
    }

    [Fact]
    public async Task RoutedPublicationIsPersistentAndCarriesExactUpdate()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.AmqpUri) }
            .CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareAsync(channel, CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitTopology.InboundQueue);

        await using var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
        await publisher.PublishConfirmedAsync(
            new(1, 73, 8, 9, DateTimeOffset.UtcNow, "hi", null, null, null, null),
            CancellationToken.None);

        var delivery = await channel.BasicGetAsync(RabbitTopology.InboundQueue, autoAck: false);
        Assert.NotNull(delivery);
        Assert.True(delivery.BasicProperties.Persistent);
        Assert.Equal(73L, JsonSerializer.Deserialize<InboundTelegramUpdate>(delivery.Body.Span)!.UpdateId);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
    }

    [Fact]
    public async Task MandatoryUnroutablePublishFailsDespiteConfirm()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.AmqpUri) }
            .CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync("test.unbound", type: "direct", durable: true, autoDelete: false);

        await using var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, "test.unbound", TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<UnroutablePublishException>(() => publisher.PublishConfirmedAsync(
            new(1, 7, 8, 9, DateTimeOffset.UtcNow, "hi", null, null, null, null),
            CancellationToken.None));
        Assert.True(error.ReturnObserved);
        Assert.True(error.BrokerAckObserved);
    }

    [Fact]
    public async Task ConnectionLossBeforeConfirmCannotReportSuccess()
    {
        var isolated = new BrokerFixture(ReserveAvailablePort());
        await isolated.InitializeAsync();
        try
        {
            await using var connection = await new ConnectionFactory { Uri = new Uri(isolated.AmqpUri) }
                .CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await RabbitTopology.DeclareAsync(channel, CancellationToken.None);
            await using var publisher = await RabbitInboundPublisher.ConnectAsync(
                isolated.AmqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
            await publisher.PublishConfirmedAsync(
                new(1, 74, 8, 9, DateTimeOffset.UtcNow, "before outage", null, null, null, null),
                CancellationToken.None);
            await isolated.Container.StopAsync();
            await Assert.ThrowsAsync<PublishNotConfirmedException>(() => publisher.PublishConfirmedAsync(
                new(1, 75, 8, 9, DateTimeOffset.UtcNow, "during outage", null, null, null, null),
                CancellationToken.None));
            await isolated.Container.StartAsync();
            await publisher.PublishConfirmedAsync(
                new(1, 76, 8, 9, DateTimeOffset.UtcNow, "after outage", null, null, null, null),
                CancellationToken.None);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private static int ReserveAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task CancellationCannotReportConfirmedAcceptance()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.AmqpUri) }
            .CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareAsync(channel, CancellationToken.None);
        await using var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<PublishNotConfirmedException>(() => publisher.PublishConfirmedAsync(
            new(1, 75, 8, 9, DateTimeOffset.UtcNow, "hi", null, null, null, null),
            cancellation.Token));
        await publisher.PublishConfirmedAsync(
            new(1, 77, 8, 9, DateTimeOffset.UtcNow, "after cancellation", null, null, null, null),
            CancellationToken.None);
    }

    [Fact]
    public async Task TimedOutInFlightPublishCanBeFollowedByFreshConfirmedPublish()
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(broker.AmqpUri) }
            .CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareAsync(channel, CancellationToken.None);
        await using var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
        await broker.Container.PauseAsync();
        var unpause = Task.Run(async () =>
        {
            await Task.Delay(300);
            await broker.Container.UnpauseAsync();
        });
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<PublishNotConfirmedException>(() => publisher.PublishConfirmedAsync(
                new(1, 80, 8, 9, DateTimeOffset.UtcNow, "uncertain", null, null, null, null),
                cancellation.Token));
            await unpause;
            await publisher.PublishConfirmedAsync(
                new(1, 81, 8, 9, DateTimeOffset.UtcNow, "confirmed later", null, null, null, null),
                CancellationToken.None);
        }
        finally
        {
            await unpause;
        }
    }

    [Fact]
    public async Task HostedPublisherStopsAndRejectsFurtherPublication()
    {
        var service = new RabbitPublisherHostedService(broker.AmqpUri, TimeSpan.FromSeconds(5));
        await service.StartAsync(CancellationToken.None);
        await service.PublishConfirmedAsync(
            new(1, 78, 8, 9, DateTimeOffset.UtcNow, "before stop", null, null, null, null),
            CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PublishNotConfirmedException>(() => service.PublishConfirmedAsync(
            new(1, 79, 8, 9, DateTimeOffset.UtcNow, "after stop", null, null, null, null),
            CancellationToken.None));
    }
}
