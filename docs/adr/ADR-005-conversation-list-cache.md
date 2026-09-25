# ADR-005: Conversation list cache

**Status:** Accepted and implemented.

## Context

`GET /api/conversations` (no cursor) is the hottest read in the system: it loads on every operator session and is refetched by every open tab on every realtime `messageStored` event (inbound message, operator reply, or mark-read). Each refetch re-runs the conversation query plus the per-conversation unread-count aggregation. Stale data here is unacceptable — an operator must see accurate previews and unread counts — but a short-lived, correctly invalidated cache removes repeated load from the hot path without adding infrastructure.

## Decision

Cache only the first (no-cursor) page of the conversation list, at its default size, using `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`) registered with `AddHybridCache()` and no distributed L2 — in-memory only, matching the single-instance deployment. Cursor pages and non-default `limit` values always read the database directly.

Invalidation is event-driven: `CacheInvalidatingInboxNotifier` decorates the existing `IInboxNotifier` and removes the cached entry by its exact key *before* delegating to the real (SignalR) notifier. All three write paths that affect the list — inbound message stored, operator reply persisted, conversation marked read — already funnel through this single `IInboxNotifier` call, so one decorator invalidates all of them without touching the use cases. Because the eviction happens first, a client's realtime-triggered refetch can never observe stale data. Each caller already wraps the notifier call in a try/catch that logs and swallows failures, so a cache-eviction failure is covered by that existing "notifications never block persistence" policy — no new error handling was needed.

A 30-second absolute expiration is kept as a safety net in case an invalidation is ever missed, not as the primary freshness mechanism.

Cache eviction uses an exact key (`cache.RemoveAsync`), not `HybridCache`'s tag feature. Tag-based removal (`RemoveByTagAsync`) is timestamp-based ("ignore entries created before this point") and was verified during implementation to miss entries created in the same tick as the invalidation call (a real, reported HybridCache limitation — dotnet/aspnetcore#58857) — an integration test written against tags failed intermittently. Exact-key removal has no such race.

## Alternatives and tradeoffs

**No cache.** Simplest option; rejected because the list query (plus its unread-count aggregation) reruns on every tab's every realtime event, for no measured benefit of skipping it.

**Redis as a distributed L2 (`HybridCache` + `AddStackExchangeRedisCache`).** Needed for multiple API instances sharing one cache and for cross-instance invalidation. Rejected for now: it adds a Compose service, new failure modes (Redis outage, network partition), and a C4 diagram change, for a single-operator, single-instance deployment with no measured need. `HybridCache`'s API already isolates this decision to configuration — adding Redis later needs no application code change.

**Output caching (`AddOutputCache` + `.CacheOutput()`).** Caches the whole HTTP response and is simpler to wire, but its tag-based eviction (`IOutputCacheStore.EvictByTagAsync`) is a coarser, less controllable mechanism for this specific need (evicting one exact logical page from inside a notifier decorator), and mixing it with the existing SignalR notification path would be less direct than a single `IInboxNotifier` decorator.

## Consequences

- **Positive:** the hot path (default first page) is served from memory between writes; invalidation is centralized in one decorator instead of duplicated across three use cases; no new infrastructure or configuration surface.
- **Negative:** an operator hitting the API directly with a non-default `limit` on the first page always bypasses the cache (acceptable: the UI never does this).
- **Scaling note:** if the API is ever run as multiple instances, each instance's in-memory cache and each browser tab's SignalR connection are local to that instance. At that point `HybridCache` would need Redis as its L2, and SignalR would need a backplane (e.g. Redis backplane) so a write on one instance invalidates and notifies clients connected to every other instance. Neither is needed for the current single-instance deployment.
