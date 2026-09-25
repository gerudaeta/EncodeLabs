namespace ChatInbox.Application.Inbound;

public sealed record InboundTelegramUpdate(
    int Version,
    long UpdateId,
    long ChatId,
    long MessageId,
    DateTimeOffset SentAt,
    string Text,
    long? SenderId,
    string? SenderFirstName,
    string? SenderLastName,
    string? ChatTitle);
