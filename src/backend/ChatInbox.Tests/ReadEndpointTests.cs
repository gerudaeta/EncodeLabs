using System.Net;
using ChatInbox.Application.Inbound;
using ChatInbox.Domain;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatInbox.Tests;

public sealed class ReadEndpointTests(PostgresFixture postgres, BrokerFixture broker)
    : IClassFixture<PostgresFixture>, IClassFixture<BrokerFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = null!;
    private readonly DateTimeOffset _at = DateTimeOffset.Parse("2026-09-25T10:00:00Z");

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)
            .UseSetting("RabbitMQ:Uri", broker.AmqpUri)
            .UseSetting("Telegram:WebhookSecret", "test_secret_123")
            .UseSetting("Telegram:RegistrationEnabled", "false")
            .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"));
        await using (var db = OpenDb())
            await db.Database.MigrateAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task UnknownConversationIsNotFound()
    {
        using var response = await _client.PostAsync($"/api/conversations/{Guid.NewGuid()}/read", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MarksUnreadInboundMessagesReadAndIsIdempotent()
    {
        var chat = Random.Shared.NextInt64(1, long.MaxValue);
        await SeedAsync(chat, 1, _at, "hello");
        var conversation = await ConversationIdAsync(chat);

        using var first = await _client.PostAsync($"/api/conversations/{conversation}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        await using var db = OpenDb();
        var message = await db.Messages.SingleAsync(m => m.ConversationId == conversation);
        Assert.NotNull(message.ReadAt);

        using var second = await _client.PostAsync($"/api/conversations/{conversation}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        db.ChangeTracker.Clear();
        var stillReadAt = await db.Messages.SingleAsync(m => m.ConversationId == conversation);
        Assert.Equal(message.ReadAt, stillReadAt.ReadAt);
    }

    [Fact]
    public async Task OutboundMessagesAreUnaffectedByMarkRead()
    {
        var chat = Random.Shared.NextInt64(1, long.MaxValue);
        var conversation = Guid.NewGuid();
        await using (var db = OpenDb())
        {
            db.Conversations.Add(new Conversation { Id = conversation, TelegramChatId = chat });
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(), ConversationId = conversation, TelegramMessageId = 1,
                Text = "reply", SentAt = _at, Direction = MessageDirection.Outbound
            });
            await db.SaveChangesAsync();
        }

        using var response = await _client.PostAsync($"/api/conversations/{conversation}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var readDb = OpenDb();
        var message = await readDb.Messages.SingleAsync(m => m.ConversationId == conversation);
        Assert.Null(message.ReadAt);
    }

    [Fact]
    public async Task MarkingReadNotifiesConnectedClients()
    {
        var chat = Random.Shared.NextInt64(1, long.MaxValue);
        await SeedAsync(chat, 1, _at, "hello");
        var conversation = await ConversationIdAsync(chat);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory!.Server.BaseAddress, "/hubs/inbox"), options =>
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler())
            .Build();
        var received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = connection.On<JsonConversationId>("messageStored",
            payload => received.TrySetResult(payload.ConversationId));
        await connection.StartAsync();

        using var response = await _client.PostAsync($"/api/conversations/{conversation}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var notified = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(conversation, notified);
    }

    private sealed record JsonConversationId(Guid ConversationId);

    private async Task SeedAsync(long chat, long messageId, DateTimeOffset at, string text)
    {
        await using var db = OpenDb();
        await new InboxRepository(db).StoreAsync(new InboundTelegramUpdate(1,
            Random.Shared.NextInt64(1, long.MaxValue), chat, messageId, at,
            text, null, null, null, null), CancellationToken.None);
    }

    private async Task<Guid> ConversationIdAsync(long chat)
    {
        await using var db = OpenDb();
        return await db.Conversations.Where(c => c.TelegramChatId == chat).Select(c => c.Id).SingleAsync();
    }

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);
}
