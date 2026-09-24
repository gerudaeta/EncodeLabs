using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;

namespace ChatInbox.Tests;

public sealed class ConsumerReadinessTests(BrokerFixture broker) : IClassFixture<BrokerFixture>
{
    [Fact]
    public async Task ReadinessFollowsSuccessfulSubscriptionAndHostShutdown()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(broker.AmqpUri) }
            .CreateConnectionAsync();
        await using (var channel = await connection.CreateChannelAsync())
            await RabbitTopology.DeclareAsync(channel, CancellationToken.None);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(connection);
        builder.Services.AddSingleton<IInboundStore, NoopStore>();
        builder.Services.AddSingleton<InboundConsumer>();
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<InboundConsumer>());
        using var host = builder.Build();
        var consumer = host.Services.GetRequiredService<InboundConsumer>();
        Assert.False(consumer.IsReady);

        try
        {
            await host.StartAsync();
            await consumer.WaitReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            Assert.True(consumer.IsReady);
            await host.StopAsync();
            Assert.False(consumer.IsReady);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private sealed class NoopStore : IInboundStore
    {
        public Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken) =>
            Task.FromResult(StoreOutcome.Inserted);
    }
}
