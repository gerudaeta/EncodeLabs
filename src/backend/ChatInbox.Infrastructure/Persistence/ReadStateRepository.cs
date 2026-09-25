using ChatInbox.Application.Read;
using Microsoft.EntityFrameworkCore;

namespace ChatInbox.Infrastructure.Persistence;

public sealed class ReadStateRepository(InboxDbContext db) : IReadStateRepository
{
    public async Task<MarkReadOutcome> MarkConversationReadAsync(Guid conversationId,
        CancellationToken cancellationToken)
    {
        var exists = await db.Conversations.AsNoTracking()
            .AnyAsync(c => c.Id == conversationId, cancellationToken);
        if (!exists) return MarkReadOutcome.NotFound;

        var unread = await db.Messages
            .Where(m => m.ConversationId == conversationId &&
                m.Direction == MessageDirections.Inbound && m.ReadAt == null)
            .ToListAsync(cancellationToken);
        if (unread.Count == 0) return MarkReadOutcome.NoChange;

        var now = DateTimeOffset.UtcNow;
        foreach (var message in unread) message.MarkRead(now);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return MarkReadOutcome.Marked;
    }
}
