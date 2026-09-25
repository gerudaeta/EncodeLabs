using Microsoft.Extensions.Caching.Hybrid;

namespace ChatInbox.Infrastructure.Persistence;

/// <summary>
/// Cache coordinates for the conversation list's first page. The query side
/// (<see cref="InboxQueries"/>) reads/writes under <see cref="Key"/>; the notifier side
/// (<see cref="CacheInvalidatingInboxNotifier"/>) removes that exact key whenever the list
/// changes.
/// </summary>
/// <remarks>
/// Deliberately keyed on a single well-known string rather than per-limit: HybridCache's
/// tag-based removal is timestamp-based and can miss entries created in the same tick as the
/// invalidation (see dotnet/aspnetcore#58857), so exact-key removal is used instead. Only the
/// default page size is cached (see <see cref="CachedLimit"/>); other limits (only ever used by
/// pagination callers, never the UI's default load) bypass the cache entirely.
/// </remarks>
internal static class ConversationListCache
{
    // Must match InboxEndpoints.TryParameters' default `count`.
    public const int CachedLimit = 50;
    public const string Key = "conversation-list:first-page";

    // Safety-net TTL only: real freshness comes from explicit invalidation on every write path.
    public static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };
}
