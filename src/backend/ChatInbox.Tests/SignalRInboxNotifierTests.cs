using ChatInbox.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace ChatInbox.Tests;

public sealed class SignalRInboxNotifierTests
{
    [Fact]
    public async Task BroadcastsMessageStoredWithConversationIdToAllClients()
    {
        var clientProxy = new RecordingClientProxy();
        var notifier = new SignalRInboxNotifier(new FakeHubContext(new FakeHubClients(clientProxy)));
        var conversationId = Guid.NewGuid();

        await notifier.NotifyMessageStoredAsync(conversationId, CancellationToken.None);

        Assert.Equal("messageStored", clientProxy.LastMethod);
        var payload = Assert.Single(clientProxy.LastArgs!);
        Assert.Equal(conversationId, payload!.GetType().GetProperty("conversationId")!.GetValue(payload));
    }

    private sealed class FakeHubContext(IHubClients clients) : IHubContext<InboxHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class FakeHubClients(IClientProxy all) : IHubClients
    {
        public IClientProxy All { get; } = all;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
        public IClientProxy Client(string connectionId) => throw new NotImplementedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
        public IClientProxy Group(string groupName) => throw new NotImplementedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotImplementedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
        public IClientProxy OthersInGroup(string groupName) => throw new NotImplementedException();
        public IClientProxy User(string userId) => throw new NotImplementedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public string? LastMethod { get; private set; }
        public object?[]? LastArgs { get; private set; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            LastMethod = method;
            LastArgs = args;
            return Task.CompletedTask;
        }
    }
}
