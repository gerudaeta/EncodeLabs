namespace ChatInbox.Domain;

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long TelegramMessageId { get; set; }
    public long? TelegramUpdateId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public MessageDirection Direction { get; set; } = MessageDirection.Inbound;
    public DateTimeOffset? ReadAt { get; set; }

    // Domain rule: only unread inbound messages can transition to read. Outbound messages are
    // never unread, and marking an already-read message is a no-op (idempotent).
    public bool MarkRead(DateTimeOffset at)
    {
        if (Direction != MessageDirection.Inbound || ReadAt is not null) return false;
        ReadAt = at;
        return true;
    }
}
