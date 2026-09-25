using ChatInbox.Application.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace ChatInbox.Infrastructure.Persistence;

public sealed class InboxQueries(InboxDbContext db, HybridCache cache) : IInboxQueries
{
    public async Task<Page<ConversationDto>> ListConversationsAsync(int limit, PageCursor? before,
        CancellationToken cancellationToken)
    {
        // Only the default-sized first page is cached (see ConversationListCache remarks);
        // cursor pages and non-default limits always read the database directly.
        if (before is not null || limit != ConversationListCache.CachedLimit)
            return await LoadConversationsPageAsync(limit, before, cancellationToken);

        return await cache.GetOrCreateAsync(
            ConversationListCache.Key,
            this,
            static async (queries, ct) =>
                await queries.LoadConversationsPageAsync(ConversationListCache.CachedLimit, null, ct),
            ConversationListCache.EntryOptions,
            cancellationToken: cancellationToken);
    }

    private async Task<Page<ConversationDto>> LoadConversationsPageAsync(int limit, PageCursor? before,
        CancellationToken cancellationToken)
    {
        // Keep the active path sargable against ix_conversations_activity. Empty
        // conversations have no activity and are appended after active rows.
        var rows = new List<Conversation>(limit + 1);
        if (before is null || before.Timestamp > DateTimeOffset.MinValue)
        {
            var active = db.Conversations.AsNoTracking().Where(c => c.LastMessageAt != null);
            if (before is not null)
                active = active.Where(c => c.LastMessageAt < before.Timestamp ||
                    (c.LastMessageAt == before.Timestamp && c.Id.CompareTo(before.Id) < 0));
            rows.AddRange(await active.OrderByDescending(c => c.LastMessageAt)
                .ThenByDescending(c => c.Id).Take(limit + 1).ToArrayAsync(cancellationToken));
        }
        if (rows.Count < limit + 1)
        {
            var empty = db.Conversations.AsNoTracking().Where(c => c.LastMessageAt == null);
            if (before is { Timestamp: var timestamp } && timestamp == DateTimeOffset.MinValue)
                empty = empty.Where(c => c.Id.CompareTo(before.Id) < 0);
            rows.AddRange(await empty.OrderByDescending(c => c.Id)
                .Take(limit + 1 - rows.Count).ToArrayAsync(cancellationToken));
        }
        var selected = rows.Take(limit).ToArray();
        var next = rows.Count > limit
            ? PageCursor.Encode(selected[^1].LastMessageAt ?? DateTimeOffset.MinValue,
                selected[^1].Id) : null;
        var ids = selected.Select(c => c.Id).ToArray();
        var unreadCounts = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.ConversationId) &&
                m.Direction == MessageDirections.Inbound && m.ReadAt == null)
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Count, cancellationToken);
        return new Page<ConversationDto>(selected.Select(c => new ConversationDto(c.Id,
            c.TelegramChatId, c.DisplayName, c.LastMessageAt, c.LastMessagePreview,
            unreadCounts.GetValueOrDefault(c.Id))).ToArray(), next);
    }

    public async Task<Page<MessageDto>> ListMessagesAsync(Guid conversationId, int limit,
        PageCursor? before, CancellationToken cancellationToken)
    {
        if (!await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == conversationId,
                cancellationToken))
            throw new ConversationNotFoundException();

        var query = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (before is not null)
            query = query.Where(m => m.SentAt < before.Timestamp ||
                (m.SentAt == before.Timestamp && m.Id.CompareTo(before.Id) < 0));

        var rows = await query.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
            .Take(limit + 1).ToArrayAsync(cancellationToken);
        var selected = rows.Take(limit).ToArray();
        var next = rows.Length > limit
            ? PageCursor.Encode(selected[^1].SentAt, selected[^1].Id) : null;
        return new Page<MessageDto>(selected.Reverse().Select(m => new MessageDto(m.Id,
            m.TelegramMessageId, m.Direction, m.Text, m.SentAt)).ToArray(), next);
    }
}
