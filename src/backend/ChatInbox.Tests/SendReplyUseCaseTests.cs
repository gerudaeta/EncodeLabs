using ChatInbox.Application.Outbound;
using ChatInbox.Application.Queries;
using ChatInbox.Application.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ChatInbox.Tests;

public sealed class SendReplyUseCaseTests
{
    private static readonly DateTimeOffset SentAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z");

    [Fact]
    public async Task SendsThroughTelegramBeforePersistingAndNotifies()
    {
        var repository = new FakeReplyRepository { ChatId = 555 };
        var sender = new FakeReplySender { Result = new SentTelegramMessage(42, SentAt) };
        var notifier = new FakeInboxNotifier();
        var conversationId = Guid.NewGuid();
        var useCase = new SendReplyUseCase(repository, sender, notifier, NullLogger<SendReplyUseCase>.Instance);

        var result = await useCase.SendAsync(conversationId, "hello", CancellationToken.None);

        Assert.Equal(555, sender.LastChatId);
        Assert.Equal("hello", sender.LastText);
        Assert.Equal(conversationId, repository.LastAppendConversationId);
        Assert.Equal(42, result.TelegramMessageId);
        Assert.Equal(conversationId, notifier.LastConversationId);
    }

    [Fact]
    public async Task UnknownConversationNeverCallsTelegram()
    {
        var repository = new FakeReplyRepository { ChatId = null };
        var sender = new FakeReplySender();
        var useCase = new SendReplyUseCase(repository, sender, new FakeInboxNotifier(),
            NullLogger<SendReplyUseCase>.Instance);

        await Assert.ThrowsAsync<ConversationNotFoundException>(() =>
            useCase.SendAsync(Guid.NewGuid(), "hello", CancellationToken.None));

        Assert.False(sender.Called);
    }

    [Fact]
    public async Task TelegramFailurePersistsNothing()
    {
        var repository = new FakeReplyRepository { ChatId = 555 };
        var sender = new FakeReplySender { Failure = new HttpRequestException("boom") };
        var useCase = new SendReplyUseCase(repository, sender, new FakeInboxNotifier(),
            NullLogger<SendReplyUseCase>.Instance);

        await Assert.ThrowsAsync<TelegramSendFailedException>(() =>
            useCase.SendAsync(Guid.NewGuid(), "hello", CancellationToken.None));

        Assert.False(repository.AppendCalled);
    }

    [Fact]
    public async Task NotifierFailureDoesNotFailTheReply()
    {
        var repository = new FakeReplyRepository { ChatId = 555 };
        var sender = new FakeReplySender { Result = new SentTelegramMessage(1, SentAt) };
        var notifier = new FakeInboxNotifier { Failure = new IOException("hub down") };
        var useCase = new SendReplyUseCase(repository, sender, notifier, NullLogger<SendReplyUseCase>.Instance);

        var result = await useCase.SendAsync(Guid.NewGuid(), "hello", CancellationToken.None);

        Assert.NotNull(result);
    }

    private sealed class FakeReplyRepository : IReplyRepository
    {
        public long? ChatId { get; set; }
        public bool AppendCalled { get; private set; }
        public Guid LastAppendConversationId { get; private set; }

        public Task<long?> FindTelegramChatIdAsync(Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(ChatId);

        public Task<MessageDto> AppendReplyAsync(Guid conversationId, SentTelegramMessage sent, string text,
            CancellationToken cancellationToken)
        {
            AppendCalled = true;
            LastAppendConversationId = conversationId;
            return Task.FromResult(new MessageDto(Guid.NewGuid(), sent.TelegramMessageId, "outbound", text,
                sent.SentAt));
        }
    }

    private sealed class FakeReplySender : IReplySender
    {
        public SentTelegramMessage Result { get; set; } = new(0, SentAt);
        public Exception? Failure { get; set; }
        public bool Called { get; private set; }
        public long LastChatId { get; private set; }
        public string? LastText { get; private set; }

        public Task<SentTelegramMessage> SendAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Called = true;
            LastChatId = chatId;
            LastText = text;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result);
        }
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
