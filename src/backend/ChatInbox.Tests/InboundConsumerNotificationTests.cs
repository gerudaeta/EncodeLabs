using ChatInbox.Application.Inbound;
using ChatInbox.Application.Realtime;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;

namespace ChatInbox.Tests;

public sealed class InboundConsumerNotificationTests(BrokerFixture broker, PostgresFixture postgres)
    : IClassFixture<BrokerFixture>, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitTopology.DeclareAsync(channel, CancellationToken.None);
        await channel.QueuePurgeAsync(RabbitTopology.InboundQueue);
        await channel.QueuePurgeAsync(RabbitTopology.DeadQueue);
        await using var db = OpenDb();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task NotifiesOnceWithTheConversationIdForANewMessageButNotForADuplicate()
    {
        var notifier = new RecordingNotifier();
        await using var connection = await ConnectAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(connection);
        builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(postgres.ConnectionString));
        builder.Services.AddScoped<IInboundStore, InboxRepository>();
        builder.Services.AddSingleton<IInboxNotifier>(notifier);
        builder.Services.AddHostedService<InboundConsumer>();
        var host = builder.Build();
        await host.StartAsync();

        var update = new InboundTelegramUpdate(1, Random.Shared.NextInt64(1, long.MaxValue), 9301, 1,
            DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "hello", null, null, null, null);
        await PublishAsync(update);
        await WaitUntilAsync(() => Task.FromResult(notifier.ConversationIds.Count == 1), TimeSpan.FromSeconds(10));

        var expectedConversationId = await ConversationIdAsync(update.ChatId);
        Assert.Equal(expectedConversationId, Assert.Single(notifier.ConversationIds));

        // Redelivering the same update commits nothing new, so it must not notify again.
        await PublishAsync(update);
        await Task.Delay(500);
        Assert.Single(notifier.ConversationIds);

        await host.StopAsync();
        host.Dispose();
    }

    private Task<IConnection> ConnectAsync() => new ConnectionFactory
    {
        Uri = new Uri(broker.AmqpUri), AutomaticRecoveryEnabled = false
    }.CreateConnectionAsync();

    private async Task PublishAsync(InboundTelegramUpdate update)
    {
        await using var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
        await publisher.PublishConfirmedAsync(update, CancellationToken.None);
    }

    private async Task<Guid> ConversationIdAsync(long chatId)
    {
        await using var db = OpenDb();
        return await db.Conversations.Where(c => c.TelegramChatId == chatId).Select(c => c.Id).SingleAsync();
    }

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!await condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, deadline.Token);
        }
    }

    private sealed class RecordingNotifier : IInboxNotifier
    {
        private readonly List<Guid> _conversationIds = [];
        public IReadOnlyList<Guid> ConversationIds { get { lock (_conversationIds) return [.. _conversationIds]; } }

        public Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            lock (_conversationIds) _conversationIds.Add(conversationId);
            return Task.CompletedTask;
        }
    }
}
