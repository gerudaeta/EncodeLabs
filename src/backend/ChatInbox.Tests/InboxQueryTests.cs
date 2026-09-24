using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace ChatInbox.Tests;

public sealed class InboxQueryTests(PostgresFixture postgres, BrokerFixture broker, ITestOutputHelper output)
    : IClassFixture<PostgresFixture>, IClassFixture<BrokerFixture>, IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = null!;
    private readonly DateTimeOffset _at = DateTimeOffset.Parse("2026-09-24T10:00:00Z");

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString)
            .UseSetting("RabbitMQ:Uri", broker.AmqpUri)
            .UseSetting("Telegram:WebhookSecret", "test_secret_123")
            .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task EqualTimestampPagesUseIdTieBreakAndRenderMessagesChronologically()
    {
        var chat = Random.Shared.NextInt64(1, long.MaxValue);
        await SeedAsync(chat, 1, _at, "one");
        await SeedAsync(chat, 2, _at, "two");
        await SeedAsync(chat, 3, _at, "three");
        var conversation = await ConversationIdAsync(chat);
        await using var db = OpenDb();
        var expected = await db.Messages.Where(m => m.ConversationId == conversation)
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
            .Select(m => m.Id).ToArrayAsync();

        var seen = new List<Guid>();
        string? before = null;
        do
        {
            using var page = await GetPageAsync($"/api/conversations/{conversation}/messages?limit=1", before);
            var items = page.RootElement.GetProperty("items");
            Assert.Single(items.EnumerateArray());
            seen.Add(items[0].GetProperty("id").GetGuid());
            before = Cursor(page);
        } while (before is not null);
        Assert.Equal(expected, seen);

        using var two = await GetPageAsync($"/api/conversations/{conversation}/messages?limit=2");
        var shown = two.RootElement.GetProperty("items").EnumerateArray()
            .Select(m => m.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(expected.Take(2).Reverse(), shown);
        Assert.All(two.RootElement.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("inbound", item.GetProperty("direction").GetString());
            Assert.Equal(_at, item.GetProperty("sentAt").GetDateTimeOffset());
        });
    }

    [Fact]
    public async Task ConversationPagesAreStableAndLateMessagesDoNotRegressActivity()
    {
        var chat1 = Random.Shared.NextInt64(1, long.MaxValue - 2);
        var chat2 = chat1 + 1;
        var chat3 = chat1 + 2;
        await SeedAsync(chat1, 1, _at, "first");
        await SeedAsync(chat2, 1, _at, "second");
        await SeedAsync(chat3, 1, _at.AddMinutes(-1), "older");
        await SeedAsync(chat3, 2, _at.AddMinutes(-2), "late");
        var id1 = await ConversationIdAsync(chat1);
        var id2 = await ConversationIdAsync(chat2);
        var id3 = await ConversationIdAsync(chat3);
        await using var expectedDb = OpenDb();
        var expected = await expectedDb.Conversations
            .OrderBy(c => c.LastMessageAt == null)
            .ThenByDescending(c => c.LastMessageAt).ThenByDescending(c => c.Id)
            .Select(c => c.Id).ToArrayAsync();

        var found = new List<Guid>();
        string? before = null;
        do
        {
            using var page = await GetPageAsync("/api/conversations?limit=1", before);
            found.Add(page.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
            before = Cursor(page);
        } while (before is not null);
        Assert.Equal(expected, found);
        Assert.True(Array.IndexOf(found.ToArray(), id1) < Array.IndexOf(found.ToArray(), id3));
        Assert.True(Array.IndexOf(found.ToArray(), id2) < Array.IndexOf(found.ToArray(), id3));
        using var response = await GetPageAsync("/api/conversations?limit=100");
        var old = response.RootElement.GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == id3);
        Assert.Equal("older", old.GetProperty("lastMessagePreview").GetString());
        Assert.Equal(_at.AddMinutes(-1), old.GetProperty("lastMessageAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task LimitsAndCursorsAreValidatedAndUnknownConversationIsNotFound()
    {
        foreach (var path in new[]
        {
            "/api/conversations?limit=0", "/api/conversations?limit=101",
            "/api/conversations?limit=abc", "/api/conversations?before=not-a-cursor",
            "/api/conversations?before=e30", "/api/conversations?before=" + new string('x', 2048)
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync(path)).StatusCode);

        var unsupportedVersion = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "{\"V\":2,\"T\":\"2026-09-24T10:00:00.0000000+00:00\",\"Id\":\"11111111-1111-1111-1111-111111111111\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/conversations?before=" + unsupportedVersion)).StatusCode);

        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/conversations/{missing}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync($"/api/conversations/{missing}/messages?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync($"/api/conversations/{missing}/messages?before=broken")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.GetAsync("/api/conversations?limit=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.GetAsync("/api/conversations?limit=100")).StatusCode);
    }

    [Fact]
    public async Task DefaultPageIsFiftyAndGetRequestsDoNotWrite()
    {
        var chat = Random.Shared.NextInt64(1, long.MaxValue - 51);
        var conversation = Guid.NewGuid();
        await using (var db = OpenDb())
        {
            db.Conversations.Add(new Conversation { Id = conversation, TelegramChatId = chat,
                LastMessageAt = _at, LastMessagePreview = "latest" });
            for (var i = 0; i < 51; i++)
                db.Messages.Add(new Message { Id = Guid.NewGuid(), ConversationId = conversation,
                    TelegramMessageId = i + 1, TelegramUpdateId = chat + i,
                    SentAt = _at.AddSeconds(i), Text = $"text {i}" });
            await db.SaveChangesAsync();
        }
        await using var readDb = OpenDb();
        var before = (await readDb.Conversations.CountAsync(), await readDb.Messages.CountAsync(),
            await readDb.ProcessedUpdates.CountAsync());
        using var page = await GetPageAsync($"/api/conversations/{conversation}/messages");
        Assert.Equal(50, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.NotNull(Cursor(page));
        var corrupted = Cursor(page)![..^1] + "!";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync($"/api/conversations/{conversation}/messages?before={Uri.EscapeDataString(corrupted)}")).StatusCode);
        using var conversations = await GetPageAsync("/api/conversations");
        Assert.NotEmpty(conversations.RootElement.GetProperty("items").EnumerateArray());
        readDb.ChangeTracker.Clear();
        var after = (await readDb.Conversations.CountAsync(), await readDb.Messages.CountAsync(),
            await readDb.ProcessedUpdates.CountAsync());
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ReadQueryPlansUsePersistedIndexes()
    {
        await SeedAsync(Random.Shared.NextInt64(1, long.MaxValue), 1, _at, "plan sample");
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("SET enable_seqscan = off; ANALYZE conversations; ANALYZE messages;", connection))
            await setup.ExecuteNonQueryAsync();

        var conversationPlan = await ExplainAsync(connection,
            "SELECT * FROM conversations WHERE last_message_at IS NOT NULL " +
            "ORDER BY last_message_at DESC, id DESC LIMIT 50");
        var messagePlan = await ExplainAsync(connection,
            "SELECT * FROM messages WHERE conversation_id = '00000000-0000-0000-0000-000000000001' " +
            "ORDER BY sent_at DESC, id DESC LIMIT 50");
        output.WriteLine("Conversation plan: {0}", conversationPlan);
        output.WriteLine("Message plan: {0}", messagePlan);
        Assert.Contains("ix_conversations_activity", conversationPlan);
        Assert.Contains("ix_messages_conversation_time", messagePlan);
    }

    [Fact]
    public async Task EmptyConversationsFollowActiveOnesAcrossCursorPages()
    {
        await SeedAsync(Random.Shared.NextInt64(1, long.MaxValue), 1, _at, "active");
        var firstEmpty = Guid.NewGuid();
        var secondEmpty = Guid.NewGuid();
        await using (var db = OpenDb())
        {
            db.Conversations.AddRange(
                new Conversation { Id = firstEmpty, TelegramChatId = -1001 },
                new Conversation { Id = secondEmpty, TelegramChatId = -1002 });
            await db.SaveChangesAsync();
        }
        var seen = new List<Guid>();
        string? before = null;
        do
        {
            using var page = await GetPageAsync("/api/conversations?limit=1", before);
            seen.Add(page.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
            before = Cursor(page);
        } while (before is not null);
        Assert.Equal(new[] { firstEmpty, secondEmpty }.OrderByDescending(x => x), seen.TakeLast(2));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, string select)
    {
        await using var command = new NpgsqlCommand("EXPLAIN " + select, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }

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

    private async Task<JsonDocument> GetPageAsync(string url, string? before = null)
    {
        if (before is not null) url += "&before=" + Uri.EscapeDataString(before);
        using var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonDocument>())!;
    }

    private static string? Cursor(JsonDocument page) =>
        page.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
            ? null : page.RootElement.GetProperty("nextCursor").GetString();
}
