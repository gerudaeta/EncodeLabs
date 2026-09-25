namespace ChatInbox.Application.Read;

public enum MarkReadOutcome
{
    NotFound,
    NoChange,
    Marked
}

public interface IReadStateRepository
{
    Task<MarkReadOutcome> MarkConversationReadAsync(Guid conversationId, CancellationToken cancellationToken);
}
