using ChatInbox.Application.Outbound;
using ChatInbox.Application.Queries;
using ChatInbox.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatInbox.Infrastructure.Persistence;

public sealed class ReplyRepository(InboxDbContext db) : IReplyRepository
{
    public async Task<long?> FindTelegramChatIdAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId)
            .Select(c => (long?)c.TelegramChatId).SingleOrDefaultAsync(cancellationToken);

    public async Task<MessageDto> AppendReplyAsync(Guid conversationId, SentTelegramMessage sent, string text,
        CancellationToken cancellationToken)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TelegramMessageId = sent.TelegramMessageId,
            TelegramUpdateId = null,
            Text = text,
            SentAt = sent.SentAt,
            Direction = MessageDirection.Outbound
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        // Mirrors InboxRepository's race-safe summary update: a reply only advances the
        // conversation preview when it is not older than what is already recorded.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE conversations SET
                last_message_at = CASE WHEN last_message_at IS NULL OR
                    ({sent.SentAt}, {sent.TelegramMessageId}) > (last_message_at, last_telegram_message_id)
                    THEN {sent.SentAt} ELSE last_message_at END,
                last_telegram_message_id = CASE WHEN last_message_at IS NULL OR
                    ({sent.SentAt}, {sent.TelegramMessageId}) > (last_message_at, last_telegram_message_id)
                    THEN {sent.TelegramMessageId} ELSE last_telegram_message_id END,
                last_message_preview = CASE WHEN last_message_at IS NULL OR
                    ({sent.SentAt}, {sent.TelegramMessageId}) > (last_message_at, last_telegram_message_id)
                    THEN {text} ELSE last_message_preview END
            WHERE id = {conversationId}
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return new MessageDto(message.Id, message.TelegramMessageId, message.Direction.ToStorageValue(),
            message.Text, message.SentAt);
    }
}
