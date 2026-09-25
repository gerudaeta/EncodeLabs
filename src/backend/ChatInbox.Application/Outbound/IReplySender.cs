namespace ChatInbox.Application.Outbound;

public sealed record SentTelegramMessage(long TelegramMessageId, DateTimeOffset SentAt);

public interface IReplySender
{
    Task<SentTelegramMessage> SendAsync(long chatId, string text, CancellationToken cancellationToken);
}
