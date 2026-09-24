using ChatInbox.Application.Inbound;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatInbox.Infrastructure.Persistence;

public sealed class InboxRepository(InboxDbContext db) : IInboundStore
{
    public async Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            db.ProcessedUpdates.Add(new ProcessedUpdate
            {
                UpdateId = update.UpdateId,
                ProcessedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);

            var displayName = update.ChatTitle ??
                string.Join(" ", new[] { update.SenderFirstName, update.SenderLastName }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (displayName.Length == 0) displayName = null;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO conversations (id, telegram_chat_id, display_name,
                    last_message_at, last_telegram_message_id, last_message_preview)
                VALUES ({Guid.NewGuid()}, {update.ChatId}, {displayName},
                    {update.SentAt}, {update.MessageId}, {update.Text})
                ON CONFLICT (telegram_chat_id) DO UPDATE SET
                    display_name = CASE WHEN conversations.last_message_at IS NULL OR
                        (EXCLUDED.last_message_at, EXCLUDED.last_telegram_message_id) >
                        (conversations.last_message_at, conversations.last_telegram_message_id)
                        THEN COALESCE(EXCLUDED.display_name, conversations.display_name)
                        ELSE conversations.display_name END,
                    last_message_at = CASE WHEN conversations.last_message_at IS NULL OR
                        (EXCLUDED.last_message_at, EXCLUDED.last_telegram_message_id) >
                        (conversations.last_message_at, conversations.last_telegram_message_id)
                        THEN EXCLUDED.last_message_at ELSE conversations.last_message_at END,
                    last_telegram_message_id = CASE WHEN conversations.last_message_at IS NULL OR
                        (EXCLUDED.last_message_at, EXCLUDED.last_telegram_message_id) >
                        (conversations.last_message_at, conversations.last_telegram_message_id)
                        THEN EXCLUDED.last_telegram_message_id ELSE conversations.last_telegram_message_id END,
                    last_message_preview = CASE WHEN conversations.last_message_at IS NULL OR
                        (EXCLUDED.last_message_at, EXCLUDED.last_telegram_message_id) >
                        (conversations.last_message_at, conversations.last_telegram_message_id)
                        THEN EXCLUDED.last_message_preview ELSE conversations.last_message_preview END
                """, cancellationToken);

            var conversationId = await db.Conversations.AsNoTracking()
                .Where(x => x.TelegramChatId == update.ChatId)
                .Select(x => x.Id).SingleAsync(cancellationToken);
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(),
                ConversationId = conversationId,
                TelegramMessageId = update.MessageId,
                TelegramUpdateId = update.UpdateId,
                Text = update.Text,
                SentAt = update.SentAt
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return StoreOutcome.Inserted;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_processed_updates" })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            if (await db.ProcessedUpdates.AsNoTracking()
                .AnyAsync(x => x.UpdateId == update.UpdateId, cancellationToken))
                return StoreOutcome.AlreadyProcessed;
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
