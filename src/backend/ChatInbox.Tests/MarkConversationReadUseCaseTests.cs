using ChatInbox.Application.Read;
using ChatInbox.Application.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatInbox.Tests;

public sealed class MarkConversationReadUseCaseTests
{
    [Fact]
    public async Task UnknownConversationReturnsFalseAndNeverNotifies()
    {
        var repository = new FakeReadStateRepository { Outcome = MarkReadOutcome.NotFound };
        var notifier = new FakeInboxNotifier();
        var useCase = new MarkConversationReadUseCase(repository, notifier,
            NullLogger<MarkConversationReadUseCase>.Instance);

        var found = await useCase.MarkReadAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(found);
        Assert.Null(notifier.LastConversationId);
    }

    [Fact]
    public async Task MarkingUnreadMessagesNotifiesClients()
    {
        var repository = new FakeReadStateRepository { Outcome = MarkReadOutcome.Marked };
        var notifier = new FakeInboxNotifier();
        var conversationId = Guid.NewGuid();
        var useCase = new MarkConversationReadUseCase(repository, notifier,
            NullLogger<MarkConversationReadUseCase>.Instance);

        var found = await useCase.MarkReadAsync(conversationId, CancellationToken.None);

        Assert.True(found);
        Assert.Equal(conversationId, notifier.LastConversationId);
    }

    [Fact]
    public async Task IdempotentCallWithNothingToMarkDoesNotNotify()
    {
        var repository = new FakeReadStateRepository { Outcome = MarkReadOutcome.NoChange };
        var notifier = new FakeInboxNotifier();
        var useCase = new MarkConversationReadUseCase(repository, notifier,
            NullLogger<MarkConversationReadUseCase>.Instance);

        var found = await useCase.MarkReadAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(found);
        Assert.Null(notifier.LastConversationId);
    }

    [Fact]
    public async Task NotifierFailureDoesNotFailTheRequest()
    {
        var repository = new FakeReadStateRepository { Outcome = MarkReadOutcome.Marked };
        var notifier = new FakeInboxNotifier { Failure = new IOException("hub down") };
        var useCase = new MarkConversationReadUseCase(repository, notifier,
            NullLogger<MarkConversationReadUseCase>.Instance);

        var found = await useCase.MarkReadAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(found);
    }

    private sealed class FakeReadStateRepository : IReadStateRepository
    {
        public MarkReadOutcome Outcome { get; set; }

        public Task<MarkReadOutcome> MarkConversationReadAsync(Guid conversationId,
            CancellationToken cancellationToken) => Task.FromResult(Outcome);
    }

    private sealed class FakeInboxNotifier : IInboxNotifier
    {
        public Exception? Failure { get; set; }
        public Guid? LastConversationId { get; private set; }

        public Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            LastConversationId = conversationId;
            if (Failure is not null) throw Failure;
            return Task.CompletedTask;
        }
    }
}
