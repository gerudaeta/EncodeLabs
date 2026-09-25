using System.Net.Http.Json;
using ChatInbox.Application.Outbound;
using ChatInbox.Domain;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatInbox.Tests;

public sealed class InboxHubTests(PostgresFixture postgres, BrokerFixture broker)
    : IClassFixture<PostgresFixture>, IClassFixture<BrokerFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HubConnection? _connection;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)
            .UseSetting("RabbitMQ:Uri", broker.AmqpUri)
            .UseSetting("Telegram:WebhookSecret", "test_secret_123")
            .UseSetting("Telegram:RegistrationEnabled", "false")
            .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")
            .ConfigureTestServices(services =>
                services.AddScoped<IReplySender>(_ => new StubReplySender())));
        await using (var db = OpenDb())
            await db.Database.MigrateAsync();

        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/inbox"), options =>
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler())
            .Build();
        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task PersistedReplyBroadcastsMessageStoredWithItsConversationId()
    {
        var received = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _connection!.On<JsonConversationId>("messageStored",
            payload => received.TrySetResult(payload.ConversationId));

        var conversationId = Guid.NewGuid();
        await using (var db = OpenDb())
        {
            db.Conversations.Add(new Conversation { Id = conversationId,
                TelegramChatId = Random.Shared.NextInt64(1, long.MaxValue) });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages",
            new { text = "hello from the operator" });
        response.EnsureSuccessStatusCode();

        var notifiedConversationId = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(conversationId, notifiedConversationId);
    }

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);

    private sealed record JsonConversationId(Guid ConversationId);

    private sealed class StubReplySender : IReplySender
    {
        public Task<SentTelegramMessage> SendAsync(long chatId, string text, CancellationToken cancellationToken) =>
            Task.FromResult(new SentTelegramMessage(1, DateTimeOffset.UtcNow));
    }
}
