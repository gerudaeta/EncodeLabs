using System.Net;
using System.Net.Http.Json;
using ChatInbox.Application.Inbound;
using ChatInbox.Application.Outbound;
using ChatInbox.Domain;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatInbox.Tests;

public sealed class ReplyEndpointTests(PostgresFixture postgres, BrokerFixture broker)
    : IClassFixture<PostgresFixture>, IClassFixture<BrokerFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = null!;
    private FakeReplySender _sender = null!;

    public async Task InitializeAsync()
    {
        _sender = new FakeReplySender();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)
            .UseSetting("RabbitMQ:Uri", broker.AmqpUri)
            .UseSetting("Telegram:WebhookSecret", "test_secret_123")
            .UseSetting("Telegram:RegistrationEnabled", "false")
            .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")
            .ConfigureTestServices(services => services.AddScoped<IReplySender>(_ => _sender)));
        await using (var db = OpenDb())
            await db.Database.MigrateAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task BlankTextIsRejected(string? text)
    {
        var conversation = await SeedConversationAsync(UniqueChatId());
        using var response = await _client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { text });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(_sender.Called);
    }

    [Fact]
    public async Task TextOverTheLimitIsRejected()
    {
        var conversation = await SeedConversationAsync(UniqueChatId());
        using var response = await _client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { text = new string('a', 4097) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(_sender.Called);
    }

    [Fact]
    public async Task UnknownConversationIsNotFoundAndTelegramIsNeverCalled()
    {
        using var response = await _client.PostAsJsonAsync($"/api/conversations/{Guid.NewGuid()}/messages",
            new { text = "hello" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(_sender.Called);
    }

    [Fact]
    public async Task TelegramFailureReturnsBadGatewayAndPersistsNothing()
    {
        var conversation = await SeedConversationAsync(UniqueChatId());
        _sender.Failure = new HttpRequestException("telegram unavailable");

        using var response = await _client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { text = "hello" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await using var db = OpenDb();
        Assert.Equal(0, await db.Messages.CountAsync(m => m.ConversationId == conversation));
    }

    [Fact]
    public async Task SuccessfulReplyIsPersistedAsOutboundAndUpdatesConversationPreview()
    {
        var chatId = UniqueChatId();
        var conversation = await SeedConversationAsync(chatId);
        _sender.Result = new SentTelegramMessage(777, DateTimeOffset.Parse("2026-09-25T11:00:00Z"));

        using var response = await _client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { text = "  a trimmed reply  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        Assert.Equal("outbound", body!.RootElement.GetProperty("direction").GetString());
        Assert.Equal("a trimmed reply", body.RootElement.GetProperty("text").GetString());
        Assert.Equal(777, body.RootElement.GetProperty("telegramMessageId").GetInt64());

        await using var db = OpenDb();
        var message = await db.Messages.SingleAsync(m => m.ConversationId == conversation);
        Assert.Equal(MessageDirection.Outbound, message.Direction);
        Assert.Null(message.TelegramUpdateId);
        var row = await db.Conversations.SingleAsync(c => c.Id == conversation);
        Assert.Equal("a trimmed reply", row.LastMessagePreview);
        Assert.Equal(777, row.LastTelegramMessageId);

        Assert.Equal(chatId, _sender.LastChatId);
        Assert.Equal("a trimmed reply", _sender.LastText);
    }

    private static long UniqueChatId() => Random.Shared.NextInt64(1, long.MaxValue);

    private async Task<Guid> SeedConversationAsync(long chatId)
    {
        var id = Guid.NewGuid();
        await using var db = OpenDb();
        db.Conversations.Add(new Conversation { Id = id, TelegramChatId = chatId });
        await db.SaveChangesAsync();
        return id;
    }

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);

    private sealed class FakeReplySender : IReplySender
    {
        public SentTelegramMessage Result { get; set; } = new(1, DateTimeOffset.UtcNow);
        public Exception? Failure { get; set; }
        public bool Called { get; private set; }
        public long LastChatId { get; private set; }
        public string? LastText { get; private set; }

        public Task<SentTelegramMessage> SendAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Called = true;
            LastChatId = chatId;
            LastText = text;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result);
        }
    }
}
