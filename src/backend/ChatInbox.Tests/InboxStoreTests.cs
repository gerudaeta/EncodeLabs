using ChatInbox.Application.Inbound;
using ChatInbox.Domain;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatInbox.Tests;

public sealed class InboxStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = OpenDb();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FullWidthIdsProduceOneMarkerConversationAndInboundMessage()
    {
        var update = NewUpdate(2147483648L, -2147483650L, 2147483649L, "hello");
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(update));

        await using var db = OpenDb();
        Assert.Equal(1, await db.ProcessedUpdates.CountAsync(x => x.UpdateId == update.UpdateId));
        var conversation = await db.Conversations.SingleAsync(x => x.TelegramChatId == update.ChatId);
        Assert.Equal(1, await db.Messages.CountAsync(x => x.ConversationId == conversation.Id &&
            x.TelegramMessageId == update.MessageId && x.TelegramUpdateId == update.UpdateId &&
            x.Direction == MessageDirection.Inbound && x.Text == "hello"));
        Assert.Equal(update.SentAt, conversation.LastMessageAt);
        Assert.Equal(update.MessageId, conversation.LastTelegramMessageId);
        Assert.Equal("hello", conversation.LastMessagePreview);
    }

    [Fact]
    public async Task SixteenConcurrentRedeliveriesHaveOneWinner()
    {
        var update = NewUpdate(301, 901, 51, "same");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => StoreAsync(update)));

        Assert.Equal(1, outcomes.Count(x => x == StoreOutcome.Inserted));
        Assert.Equal(15, outcomes.Count(x => x == StoreOutcome.AlreadyProcessed));
        await using var db = OpenDb();
        Assert.Equal(1, await db.Messages.CountAsync(x => x.TelegramUpdateId == update.UpdateId));
        Assert.Equal(1, await db.ProcessedUpdates.CountAsync(x => x.UpdateId == update.UpdateId));
    }

    [Fact]
    public async Task DifferentUpdateWithSameMessageRollsBackItsMarker()
    {
        var first = NewUpdate(401, 902, 52, "first");
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(first));
        var collision = first with { UpdateId = 402, Text = "must not commit" };

        await Assert.ThrowsAsync<DbUpdateException>(() => StoreAsync(collision));
        await using var db = OpenDb();
        Assert.False(await db.ProcessedUpdates.AnyAsync(x => x.UpdateId == collision.UpdateId));
        Assert.Equal(1, await db.Messages.CountAsync(x => x.TelegramUpdateId == first.UpdateId));
    }

    [Fact]
    public async Task LateMessageDoesNotRegressSummary()
    {
        var newer = NewUpdate(101, 900, 51, "new");
        var older = newer with { UpdateId = 102, MessageId = 50,
            SentAt = newer.SentAt.AddMinutes(-1), Text = "old" };
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(newer));
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(older));

        await using var db = OpenDb();
        var row = await db.Conversations.SingleAsync(x => x.TelegramChatId == newer.ChatId);
        Assert.Equal(newer.SentAt, row.LastMessageAt);
        Assert.Equal(newer.MessageId, row.LastTelegramMessageId);
        Assert.Equal("new", row.LastMessagePreview);
        Assert.Equal(2, await db.Messages.CountAsync(x => x.ConversationId == row.Id));
    }

    [Fact]
    public async Task EqualTimestampUsesHigherMessageIdForSummary()
    {
        var higher = NewUpdate(111, 903, 82, "higher");
        var lower = higher with { UpdateId = 112, MessageId = 81, Text = "lower" };
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(higher));
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(lower));

        await using var db = OpenDb();
        var row = await db.Conversations.SingleAsync(x => x.TelegramChatId == higher.ChatId);
        Assert.Equal(82, row.LastTelegramMessageId);
        Assert.Equal("higher", row.LastMessagePreview);
    }

    [Fact]
    public async Task StoreRemainsUsableAfterDuplicateOnSameScope()
    {
        var first = NewUpdate(501, 904, 11, "first");
        var next = NewUpdate(502, 904, 12, "next");
        await using var db = OpenDb();
        var store = new InboxRepository(db);

        Assert.Equal(StoreOutcome.Inserted, await store.StoreAsync(first, CancellationToken.None));
        Assert.Equal(StoreOutcome.AlreadyProcessed, await store.StoreAsync(first, CancellationToken.None));
        Assert.Equal(StoreOutcome.Inserted, await store.StoreAsync(next, CancellationToken.None));
        Assert.Equal(2, await db.Messages.CountAsync(x =>
            x.TelegramUpdateId == 501 || x.TelegramUpdateId == 502));
    }

    [Fact]
    public async Task ExistingConversationWithoutSummaryGetsFirstMessagePreview()
    {
        await using (var db = OpenDb())
        {
            db.Conversations.Add(new Conversation { Id = Guid.NewGuid(), TelegramChatId = 905 });
            await db.SaveChangesAsync();
        }

        var update = NewUpdate(601, 905, 13, "first");
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(update));
        await using var readDb = OpenDb();
        var row = await readDb.Conversations.SingleAsync(x => x.TelegramChatId == 905);
        Assert.Equal(update.SentAt, row.LastMessageAt);
        Assert.Equal(update.MessageId, row.LastTelegramMessageId);
        Assert.Equal("first", row.LastMessagePreview);
    }

    [Fact]
    public async Task LateMessageCannotOverwriteNewerDisplayName()
    {
        var newer = NewUpdate(701, 906, 51, "new") with { ChatTitle = "Current name" };
        var older = newer with { UpdateId = 702, MessageId = 50,
            SentAt = newer.SentAt.AddMinutes(-1), Text = "old", ChatTitle = "Stale name" };
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(newer));
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(older));

        await using var db = OpenDb();
        var row = await db.Conversations.SingleAsync(x => x.TelegramChatId == newer.ChatId);
        Assert.Equal("Current name", row.DisplayName);
        Assert.Equal("new", row.LastMessagePreview);
    }

    [Fact]
    public async Task EqualTimestampLowerMessageIdCannotOverwriteDisplayName()
    {
        var higher = NewUpdate(711, 907, 82, "higher") with { ChatTitle = "Higher name" };
        var lower = higher with { UpdateId = 712, MessageId = 81,
            Text = "lower", ChatTitle = "Lower name" };
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(higher));
        Assert.Equal(StoreOutcome.Inserted, await StoreAsync(lower));

        await using var db = OpenDb();
        var row = await db.Conversations.SingleAsync(x => x.TelegramChatId == higher.ChatId);
        Assert.Equal("Higher name", row.DisplayName);
        Assert.Equal(82, row.LastTelegramMessageId);
    }

    private async Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update)
    {
        await using var db = OpenDb();
        return await new InboxRepository(db).StoreAsync(update, CancellationToken.None);
    }

    private InboxDbContext OpenDb() => new(new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(postgres.ConnectionString).Options);

    private static InboundTelegramUpdate NewUpdate(long updateId, long chatId, long messageId,
        string text) => new(1, updateId, chatId, messageId,
            DateTimeOffset.Parse("2026-09-24T10:00:00Z"), text, null, null, null, null);
}
