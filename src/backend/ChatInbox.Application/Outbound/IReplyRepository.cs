using ChatInbox.Application.Queries;

namespace ChatInbox.Application.Outbound;

public interface IReplyRepository
{
    Task<long?> FindTelegramChatIdAsync(Guid conversationId, CancellationToken cancellationToken);

    Task<MessageDto> AppendReplyAsync(Guid conversationId, SentTelegramMessage sent, string text,
        CancellationToken cancellationToken);
}
