namespace ChatInbox.Application.Realtime;

public interface IInboxNotifier
{
    Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken);
}
