namespace ChatInbox.Domain;

public sealed class Conversation
{
    public Guid Id { get; set; }
    public long TelegramChatId { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public long? LastTelegramMessageId { get; set; }
    public string? LastMessagePreview { get; set; }
}
