using ChatInbox.Application.Realtime;
using ChatInbox.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatInbox.Tests;

public sealed class CacheInvalidatingInboxNotifierTests
{
    [Fact]
    public async Task NotifyingInvalidatesTheCachedFirstPageThenCallsTheInnerNotifier()
    {
        var cache = BuildCache();
        var inner = new RecordingNotifier();
        var notifier = new CacheInvalidatingInboxNotifier(cache, inner);
        var loads = 0;

        Task<int> LoadFirstPageAsync() => cache.GetOrCreateAsync(
            ConversationListCache.Key, 0,
            (_, _) => ValueTask.FromResult(++loads),
            ConversationListCache.EntryOptions).AsTask();

        Assert.Equal(1, await LoadFirstPageAsync());
        Assert.Equal(1, await LoadFirstPageAsync()); // still cached

        var conversationId = Guid.NewGuid();
        await notifier.NotifyMessageStoredAsync(conversationId, CancellationToken.None);

        Assert.Equal(conversationId, inner.LastConversationId);
        Assert.Equal(2, await LoadFirstPageAsync()); // cache entry was evicted, factory ran again
    }

    [Fact]
    public async Task InvalidationStillHappensWhenTheInnerNotifierFails()
    {
        var cache = BuildCache();
        var notifier = new CacheInvalidatingInboxNotifier(cache, new ThrowingNotifier());
        var loads = 0;

        Task<int> LoadFirstPageAsync() => cache.GetOrCreateAsync(
            ConversationListCache.Key, 0,
            (_, _) => ValueTask.FromResult(++loads),
            ConversationListCache.EntryOptions).AsTask();

        Assert.Equal(1, await LoadFirstPageAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.NotifyMessageStoredAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(2, await LoadFirstPageAsync()); // eviction ran before the failing inner call
    }

    private static HybridCache BuildCache() => new ServiceCollection()
        .AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();

    private sealed class RecordingNotifier : IInboxNotifier
    {
        public Guid? LastConversationId { get; private set; }

        public Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            LastConversationId = conversationId;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IInboxNotifier
    {
        public Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hub down");
    }
}
