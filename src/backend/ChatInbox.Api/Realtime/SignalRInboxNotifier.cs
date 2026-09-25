using ChatInbox.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace ChatInbox.Api.Realtime;

public sealed class SignalRInboxNotifier(IHubContext<InboxHub> hub) : IInboxNotifier
{
    public Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync("messageStored", new { conversationId }, cancellationToken);
}
