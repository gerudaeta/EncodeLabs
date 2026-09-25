using ChatInbox.Application.Realtime;
using Microsoft.Extensions.Caching.Hybrid;

namespace ChatInbox.Infrastructure.Persistence;

/// <summary>
/// Decorates the real notifier so the cached first conversation page is evicted before clients are
/// told to refetch it. All three write paths (inbound message, operator reply, mark-read) already
/// funnel through <see cref="IInboxNotifier"/> and already wrap this call in a try/catch that logs
/// and swallows failures, so a cache-eviction failure here is covered by that same policy.
/// </summary>
public sealed class CacheInvalidatingInboxNotifier(HybridCache cache, IInboxNotifier inner) : IInboxNotifier
{
    public async Task NotifyMessageStoredAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ConversationListCache.Key, cancellationToken);
        await inner.NotifyMessageStoredAsync(conversationId, cancellationToken);
    }
}
