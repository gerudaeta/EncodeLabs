using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;
using Xunit.Abstractions;

namespace ChatInbox.Tests;

public sealed class InboundConsumerTests(BrokerFixture broker, PostgresFixture postgres,
    ITestOutputHelper output)
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
    public async Task DeliveryRemainsUnackedUntilStoreCommits()
    {
        var store = new BlockingStore(postgres.ConnectionString);
        await using var consumer = await StartConsumerAsync(store);
        await PublishAsync(Update(9201));

        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(async () => await QueueCountAsync("messages_unacknowledged") == 1,
            TimeSpan.FromSeconds(10));
        await using (var before = OpenDb())
            Assert.False(await before.ProcessedUpdates.AnyAsync(x => x.UpdateId == 9201));

        store.Release.TrySetResult();
        await WaitUntilAsync(async () => await QueueCountAsync("messages_unacknowledged") == 0 &&
            await CountMessagesAsync(9201) == 1, TimeSpan.FromSeconds(10));
        Assert.Null(await GetDeadLetterAsync());
    }

    [Fact]
    public async Task CommittedDuplicateIsAcknowledgedWithoutAnotherMessage()
    {
        var store = new CountingRepositoryStore(postgres.ConnectionString);
        await using var consumer = await StartConsumerAsync(store);
        await PublishAsync(Update(9202));
        await WaitUntilAsync(async () => await CountMessagesAsync(9202) == 1 &&
            await QueueCountAsync("messages") == 0, TimeSpan.FromSeconds(10));
        await PublishAsync(Update(9202));
        await WaitUntilAsync(async () => store.Calls == 2 && await QueueCountAsync("messages") == 0,
            TimeSpan.FromSeconds(10));
        await consumer.StopAsync();

        Assert.Equal(1, await CountMessagesAsync(9202));
        Assert.Null(await GetMessageAsync(RabbitTopology.InboundQueue));
        Assert.Null(await GetDeadLetterAsync());
    }

    [Fact]
    public async Task BrokerCancellationStopsHostInsteadOfLeavingIntakeUnconsumed()
    {
        await using var consumer = await StartConsumerAsync();
        await PublishAsync(Update(9206));
        await WaitUntilAsync(async () => await CountMessagesAsync(9206) == 1,
            TimeSpan.FromSeconds(10));

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(RabbitTopology.InboundQueue);

        await WaitUntilAsync(() => Task.FromResult(consumer.ApplicationStopping),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TransientFailuresAreDelayedAndRecoverBeforeLimit()
    {
        var store = new FailingStore(failures: 2);
        await using var consumer = await StartConsumerAsync(store);
        await PublishAsync(Update(9203));

        await WaitUntilAsync(() => Task.FromResult(store.Calls >= 3), TimeSpan.FromSeconds(45));
        Assert.Equal(3, store.Calls);
        Assert.Equal(1, store.Inserted);
        Assert.True(store.Attempts[1] - store.Attempts[0] >= TimeSpan.FromMilliseconds(800),
            "First retry did not have the broker's configured delay");
        output.WriteLine($"Recovery attempts: {store.Calls}; retry gaps: {string.Join(", ",
            store.Attempts.Zip(store.Attempts.Skip(1), (first, next) =>
                (next - first).TotalMilliseconds.ToString("F0")))} ms; inserted: {store.Inserted}");
        Assert.Null(await GetDeadLetterAsync());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"Version\":2,\"UpdateId\":9204}")]
    public async Task InvalidEnvelopeIsDeadLetteredWithoutCallingStore(string body)
    {
        var store = new FailingStore();
        await using var consumer = await StartConsumerAsync(store);
        await PublishRawAsync(body);

        BasicGetResult? dead = null;
        await WaitUntilAsync(async () => (dead = await GetDeadLetterAsync()) is not null,
            TimeSpan.FromSeconds(15));
        Assert.Equal(0, store.Calls);
        Assert.NotNull(dead);
    }

    [Fact]
    public async Task ExhaustedDeliveryLimitDeadLettersWithDiagnosticHeader()
    {
        var store = new FailingStore(failures: int.MaxValue);
        await using var consumer = await StartConsumerAsync(store);
        await PublishAsync(Update(9205));

        BasicGetResult? dead = null;
        await WaitUntilAsync(async () => (dead = await GetDeadLetterAsync()) is not null,
            TimeSpan.FromSeconds(160));
        Assert.NotNull(dead);
        Assert.Equal(6, store.Calls);
        var headers = Assert.IsAssignableFrom<IDictionary<string, object?>>(dead.BasicProperties.Headers);
        var deaths = Assert.IsType<List<object>>(headers["x-death"]);
        var death = Assert.IsType<Dictionary<string, object>>(Assert.Single(deaths));
        Assert.Equal("delivery_limit", Encoding.UTF8.GetString(Assert.IsType<byte[]>(death["reason"])));
        Assert.Equal(RabbitTopology.InboundQueue,
            Encoding.UTF8.GetString(Assert.IsType<byte[]>(death["queue"])));
        Assert.Equal(RabbitTopology.InboundExchange,
            Encoding.UTF8.GetString(Assert.IsType<byte[]>(death["exchange"])));
        output.WriteLine($"Exhausted deliveries: {store.Calls}; retry gaps: {string.Join(", ",
            store.Attempts.Zip(store.Attempts.Skip(1), (first, next) =>
                (next - first).TotalMilliseconds.ToString("F0")))} ms; x-death present: true");
    }

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);

    private async Task<int> CountMessagesAsync(long updateId)
    {
        await using var db = OpenDb();
        return await db.Messages.CountAsync(x => x.TelegramUpdateId == updateId);
    }

    private Task<IConnection> ConnectAsync() => new ConnectionFactory
    {
        Uri = new Uri(broker.AmqpUri), AutomaticRecoveryEnabled = false
    }.CreateConnectionAsync();

    private async Task<ConsumerHarness> StartConsumerAsync(IInboundStore? store = null)
    {
        var connection = await ConnectAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(connection);
        if (store is null)
        {
            builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(postgres.ConnectionString));
            builder.Services.AddScoped<IInboundStore, InboxRepository>();
        }
        else builder.Services.AddSingleton(store);
        builder.Services.AddHostedService<InboundConsumer>();
        var host = builder.Build();
        await host.StartAsync();
        return new ConsumerHarness(host, connection);
    }

    private async Task PublishAsync(InboundTelegramUpdate update)
    {
        await using var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
        await publisher.PublishConfirmedAsync(update, CancellationToken.None);
    }

    private async Task PublishRawAsync(string body)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync(RabbitTopology.InboundExchange, RabbitTopology.InboundRoutingKey,
            mandatory: true, basicProperties: new BasicProperties { Persistent = true },
            body: Encoding.UTF8.GetBytes(body));
    }

    private Task<BasicGetResult?> GetDeadLetterAsync() => GetMessageAsync(RabbitTopology.DeadQueue);

    private async Task<BasicGetResult?> GetMessageAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var result = await channel.BasicGetAsync(queue, autoAck: false);
        if (result is not null) await channel.BasicNackAsync(result.DeliveryTag, false, requeue: true);
        return result;
    }

    private async Task<int> QueueCountAsync(string field, string queue = RabbitTopology.InboundQueue)
    {
        using var client = new HttpClient { BaseAddress = broker.ManagementUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(
                new Uri(broker.AmqpUri).UserInfo))));
        using var response = await client.GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty(field, out var count) ? count.GetInt32() : 0;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!await condition())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, deadline.Token);
        }
    }

    private static InboundTelegramUpdate Update(long id) => new(1, id, 80, id,
        DateTimeOffset.Parse("2026-09-24T10:00:00Z"), "test", null, null, null, null);

    private sealed class FailingStore(int failures = 0) : IInboundStore
    {
        private int _calls;
        private int _inserted;
        private readonly List<TimeSpan> _attempts = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public int Calls => Volatile.Read(ref _calls);
        public int Inserted => Volatile.Read(ref _inserted);
        public TimeSpan[] Attempts { get { lock (_attempts) return [.. _attempts]; } }

        public Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken)
        {
            lock (_attempts) _attempts.Add(_clock.Elapsed);
            var call = Interlocked.Increment(ref _calls);
            if (call <= failures) throw new IOException("transient test failure");
            Interlocked.Increment(ref _inserted);
            return Task.FromResult(StoreOutcome.Inserted);
        }
    }

    private sealed class CountingRepositoryStore(string connectionString) : IInboundStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update,
            CancellationToken cancellationToken)
        {
            await using var db = new InboxDbContext(new DbContextOptionsBuilder<InboxDbContext>()
                .UseNpgsql(connectionString).Options);
            var outcome = await new InboxRepository(db).StoreAsync(update, cancellationToken);
            Interlocked.Increment(ref _calls);
            return outcome;
        }
    }

    private sealed class BlockingStore(string connectionString) : IInboundStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await using var db = new InboxDbContext(new DbContextOptionsBuilder<InboxDbContext>()
                .UseNpgsql(connectionString).Options);
            return await new InboxRepository(db).StoreAsync(update, cancellationToken);
        }
    }

    private sealed class ConsumerHarness(IHost host, IConnection connection) : IAsyncDisposable
    {
        public bool ApplicationStopping => host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.IsCancellationRequested;

        public Task StopAsync() => host.StopAsync();

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            host.Dispose();
            await connection.DisposeAsync();
        }
    }
}
