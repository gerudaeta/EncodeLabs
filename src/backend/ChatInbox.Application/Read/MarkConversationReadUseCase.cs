using ChatInbox.Application.Realtime;
using Microsoft.Extensions.Logging;

namespace ChatInbox.Application.Read;

public sealed class MarkConversationReadUseCase(
    IReadStateRepository repository,
    IInboxNotifier notifier,
    ILogger<MarkConversationReadUseCase> logger)
{
    public async Task<bool> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var outcome = await repository.MarkConversationReadAsync(conversationId, cancellationToken);
        if (outcome == MarkReadOutcome.NotFound) return false;

        if (outcome == MarkReadOutcome.Marked)
        {
            try
            {
                await notifier.NotifyMessageStoredAsync(conversationId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Persisted read state stays authoritative; a missed realtime notification is not fatal.
                logger.LogWarning(exception,
                    "Inbox realtime notification failed for conversation {ConversationId}", conversationId);
            }
        }

        return true;
    }
}
