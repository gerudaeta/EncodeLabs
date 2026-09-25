using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatInbox.Application.Inbound;
using ChatInbox.Application.Outbound;
using ChatInbox.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatInbox.Tests.Integration;

/// <summary>
/// Proves CCH-01's acceptance criteria end to end: the default (no-cursor) first page is served
/// from cache on repeated reads, cursor pages and non-default limits always hit the database, and
/// every write path that affects the list (inbound message, operator reply, mark-read) makes the
/// next read fresh.
/// </summary>
public sealed class ConversationListCacheTests(PostgresFixture postgres, BrokerFixture broker)
    : IClassFixture<PostgresFixture>, IClassFixture<BrokerFixture>, IAsyncLifetime
{
    private const string WebhookSecret = "test_secret_123";
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = null!;
    private FakeReplySender _sender = null!;

    public async Task InitializeAsync()
    {
        _sender = new FakeReplySender();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)
            .UseSetting("RabbitMQ:Uri", broker.AmqpUri)
            .UseSetting("Telegram:WebhookSecret", WebhookSecret)
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

    [Fact]
    public async Task RepeatedFirstPageReadsAreServedFromCache()
    {
        var conversation = await SeedInboundMessageAsync(UniqueChatId(), "original preview");
        Assert.Equal("original preview", await PreviewOfConversationAsync(conversation));

        // Bypass every notifier path with a direct write: only a stale cache read explains
        // the next GET still seeing the old preview.
        await MutatePreviewDirectlyAsync(conversation, "mutated directly");

        Assert.Equal("original preview", await PreviewOfConversationAsync(conversation));
    }

    [Fact]
    public async Task CursorPagesAreNeverCached()
    {
        var conversation = await SeedInboundMessageAsync(UniqueChatId(), "first");
        await SeedInboundMessageAsync(UniqueChatId(), "second"); // pushes "first" onto page 2

        await GetConversationsAsync(); // populate the first-page cache
        using var firstPage = await GetPageAsync("/api/conversations?limit=1");
        var cursor = Cursor(firstPage);
        Assert.NotNull(cursor);

        await MutatePreviewDirectlyAsync(conversation, "mutated for cursor page");

        using var cursorPage = await GetPageAsync("/api/conversations?limit=1", cursor);
        var item = Assert.Single(cursorPage.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(conversation, item.GetProperty("id").GetGuid());
        Assert.Equal("mutated for cursor page", item.GetProperty("lastMessagePreview").GetString());
    }

    [Fact]
    public async Task NonDefaultLimitsAreNeverCached()
    {
        var conversation = await SeedInboundMessageAsync(UniqueChatId(), "first");

        await GetConversationsAsync(); // populate the default (limit=50) first-page cache
        await MutatePreviewDirectlyAsync(conversation, "mutated for a non-default limit");

        using var page = await GetPageAsync("/api/conversations?limit=1");
        var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("mutated for a non-default limit", item.GetProperty("lastMessagePreview").GetString());
    }

    [Fact]
    public async Task MarkingConversationReadInvalidatesTheCache()
    {
        var conversation = await SeedInboundMessageAsync(UniqueChatId(), "unread message");
        Assert.Equal(1, await UnreadCountOfConversationAsync(conversation));

        using var markRead = await _client.PostAsync($"/api/conversations/{conversation}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, markRead.StatusCode);

        Assert.Equal(0, await UnreadCountOfConversationAsync(conversation));
    }

    [Fact]
    public async Task PersistingAReplyInvalidatesTheCache()
    {
        var conversation = await SeedInboundMessageAsync(UniqueChatId(), "first");
        Assert.Equal("first", await PreviewOfConversationAsync(conversation));
        _sender.Result = new SentTelegramMessage(500, DateTimeOffset.UtcNow);

        using var reply = await _client.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { text = "operator reply" });
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);

        Assert.Equal("operator reply", await PreviewOfConversationAsync(conversation));
    }

    [Fact]
    public async Task InboundMessageInvalidatesTheCache()
    {
        var chatId = UniqueChatId();
        var conversation = await SeedInboundMessageAsync(chatId, "first");
        Assert.Equal("first", await PreviewOfConversationAsync(conversation));

        var updateId = Random.Shared.NextInt64(10_000_000, 1_000_000_000);
        using var webhook = new HttpRequestMessage(HttpMethod.Post, "/webhooks/telegram")
        {
            Content = JsonContent.Create(new
            {
                update_id = updateId,
                message = new
                {
                    message_id = updateId + 1,
                    date = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(), // after the seeded message
                    chat = new { id = chatId },
                    text = "second inbound message"
                }
            })
        };
        webhook.Headers.Add("X-Telegram-Bot-Api-Secret-Token", WebhookSecret);
        using var response = await _client.SendAsync(webhook);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitUntilAsync(async () =>
                await PreviewOfConversationAsync(conversation) == "second inbound message",
            TimeSpan.FromSeconds(20),
            "the first page never reflected the inbound message (cache not invalidated?)");
    }

    private static long UniqueChatId() => Random.Shared.NextInt64(1, long.MaxValue);

    private async Task<Guid> SeedInboundMessageAsync(long chatId, string text)
    {
        await using (var db = OpenDb())
            await new InboxRepository(db).StoreAsync(new InboundTelegramUpdate(1,
                Random.Shared.NextInt64(1, long.MaxValue), chatId, 1, DateTimeOffset.UtcNow,
                text, null, null, null, null), CancellationToken.None);
        await using var readDb = OpenDb();
        return await readDb.Conversations.Where(c => c.TelegramChatId == chatId)
            .Select(c => c.Id).SingleAsync();
    }

    private async Task MutatePreviewDirectlyAsync(Guid conversation, string preview)
    {
        await using var db = OpenDb();
        var row = await db.Conversations.SingleAsync(c => c.Id == conversation);
        row.LastMessagePreview = preview;
        await db.SaveChangesAsync();
    }

    private async Task<string?> PreviewOfConversationAsync(Guid conversation)
    {
        using var page = await GetConversationsAsync();
        return ItemFor(page, conversation).GetProperty("lastMessagePreview").GetString();
    }

    private async Task<int> UnreadCountOfConversationAsync(Guid conversation)
    {
        using var page = await GetConversationsAsync();
        return ItemFor(page, conversation).GetProperty("unreadCount").GetInt32();
    }

    private static JsonElement ItemFor(JsonDocument page, Guid conversation) =>
        page.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == conversation);

    private Task<JsonDocument> GetConversationsAsync() => GetPageAsync("/api/conversations");

    private async Task<JsonDocument> GetPageAsync(string url, string? before = null)
    {
        if (before is not null) url += "&before=" + Uri.EscapeDataString(before);
        using var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string? Cursor(JsonDocument page) =>
        page.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
            ? null : page.RootElement.GetProperty("nextCursor").GetString();

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, string failure)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!await predicate())
                await Task.Delay(100, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(failure);
        }
    }

    private sealed class FakeReplySender : IReplySender
    {
        public SentTelegramMessage Result { get; set; } = new(1, DateTimeOffset.UtcNow);

        public Task<SentTelegramMessage> SendAsync(long chatId, string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }
}
