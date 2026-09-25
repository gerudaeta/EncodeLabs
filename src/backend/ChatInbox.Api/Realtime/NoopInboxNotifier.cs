using ChatInbox.Application.Realtime;

namespace ChatInbox.Api.Realtime;

/// <summary>
/// Placeholder used until the SignalR hub notifier lands; keeps the reply pipeline usable
/// on its own without a realtime transport.
/// </summary>
public sealed class NoopInboxNotifier : IInboxNotifier
{
    public Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
