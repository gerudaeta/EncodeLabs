using ChatInbox.Application.Queries;
using ChatInbox.Application.Realtime;
using Microsoft.Extensions.Logging;

namespace ChatInbox.Application.Outbound;

public sealed class TelegramSendFailedException : Exception;

public sealed class SendReplyUseCase(
    IReplyRepository repository,
    IReplySender sender,
    IInboxNotifier notifier,
    ILogger<SendReplyUseCase> logger)
{
    public async Task<MessageDto> SendAsync(Guid conversationId, string text, CancellationToken cancellationToken)
    {
        var chatId = await repository.FindTelegramChatIdAsync(conversationId, cancellationToken)
            ?? throw new ConversationNotFoundException();

        SentTelegramMessage sent;
        try
        {
            sent = await sender.SendAsync(chatId, text, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new TelegramSendFailedException();
        }

        var message = await repository.AppendReplyAsync(conversationId, sent, text, cancellationToken);

        try
        {
            await notifier.NotifyMessageStoredAsync(conversationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Persisted data stays authoritative; a missed realtime notification is not fatal.
            logger.LogWarning(exception, "Inbox realtime notification failed for conversation {ConversationId}",
                conversationId);
        }

        return message;
    }
}
