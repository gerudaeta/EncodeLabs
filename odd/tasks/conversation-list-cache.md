# Conversation list cache

## Objective
Cache the first page of the conversation list with .NET `HybridCache` (in-memory only), invalidated on every state change that affects the list, and document the decision in ADR-005.

## Problem / why
The conversation list is the most frequently read query (loaded on startup and refetched on every realtime `messageStored` notification from every open tab). A small, correctly invalidated in-process cache demonstrates the caching strategy asked about in the tech test (bonus question 1) without adding infrastructure. Redis was explicitly deferred: it would add a Compose service, failure modes and C4 changes with no measured need for a single operator.

## Scope
- Register `HybridCache` (Microsoft.Extensions.Caching.Hybrid) with no distributed L2.
- Cache only the first page (no cursor) of `GET /api/conversations`; cursor pages bypass the cache.
- Invalidate on every change that affects the list: inbound message stored, operator reply persisted, conversation marked read. Prefer a single invalidation point that all those paths already go through (e.g. where `messageStored` is notified) if the existing design allows it cleanly; invalidation must happen before clients are notified so their refetch never reads stale data.
- Short absolute expiration as a safety net (e.g. 30 s).
- ADR-005 in `docs/adr/ADR-005-conversation-list-cache.md` following the format of existing ADRs, mentioning Redis as a future L2 / SignalR backplane when scaling out.

## Constraints
- Clean Architecture boundaries as in existing code; no new Compose services.
- Stale unread counts or previews are a correctness bug: tests must prove invalidation.

## TDD
Enabled: RED → GREEN → REFACTOR. Backend runner `dotnet test src/backend/ChatInbox.slnx`.

## Tasks
- [x] CCH-01 Cache the first conversation page with HybridCache + invalidation on inbound/reply/read, tests.
- [x] CCH-02 ADR-005.
- [x] CCH-03 Verify: full suite, Docker rebuild of api, live check that list reflects new messages and read state.

## Acceptance criteria
- Repeated first-page reads hit the cache (observable in tests).
- After an inbound message, a reply, or mark-read, the next first-page read returns fresh data.

## Progress / evidence

**CCH-01 — done.**
- `HybridCache` registered in `src/backend/ChatInbox.Api/Program.cs` via `AddHybridCache()`, no distributed L2. `InboxQueries.ListConversationsAsync` (`src/backend/ChatInbox.Infrastructure/Persistence/InboxQueries.cs`) caches only the default-limit (50), no-cursor first page under a single well-known key (`ConversationListCache.Key`); cursor pages and non-default limits always hit the database.
- `CacheInvalidatingInboxNotifier` (`src/backend/ChatInbox.Infrastructure/Persistence/CacheInvalidatingInboxNotifier.cs`) decorates `IInboxNotifier`, removing the cached key before delegating to the real (SignalR) notifier. All three write paths (`InboundConsumer`, `SendReplyUseCase`, `MarkConversationReadUseCase`) already funnel through this one interface, so this is the single invalidation point, and each caller already wraps the notifier call in try/catch (notification failures are logged, never fatal) — invalidation inherits that same safety net for free.
- **Design finding during TDD:** `HybridCache.RemoveByTagAsync` (tag-based invalidation) was tried first but is timestamp-based and can miss an entry created in the same tick as the invalidation (confirmed empirically — a test written against it failed even with the real removal call in place; this matches the known issue dotnet/aspnetcore#58857). Switched to exact-key `RemoveAsync`, which is deterministic and closed the gap. Documented in ADR-005.
- TDD evidence: RED confirmed twice — (1) `CacheInvalidatingInboxNotifierTests` failed with the invalidation call stubbed out; (2) all of `MarkingConversationReadInvalidatesTheCache`, `PersistingAReplyInvalidatesTheCache`, `InboundMessageInvalidatesTheCache` in `ConversationListCacheTests` failed the same way. Restored the real implementation → GREEN.
- Tests added: `src/backend/ChatInbox.Tests/CacheInvalidatingInboxNotifierTests.cs` (unit, decorator invalidates + still invalidates when the inner notifier throws) and `src/backend/ChatInbox.Tests/Integration/ConversationListCacheTests.cs` (Postgres+RabbitMQ integration: repeated-read cache hit, cursor pages never cached, non-default limits never cached, and freshness after mark-read / reply / real inbound webhook→RabbitMQ→consumer flow).
- `dotnet test src/backend/ChatInbox.slnx`: 118 passed, 0 failed, 0 skipped (full suite, after a clean rebuild).

**CCH-02 — done.** `docs/adr/ADR-005-conversation-list-cache.md` written in ADR-004's format (Status/Context/Decision/Alternatives and tradeoffs/Consequences), covering the tag-vs-exact-key finding and the future Redis L2 + SignalR backplane note for multi-instance scaling.

**CCH-03 — done.** `docker compose up -d --build api` OK. Live: first page read (cached) showed preview
"Jckdndnd"; operator reply `POST /api/conversations/{id}/messages` → 201; the immediate next read showed
preview "cache check" (fresh after invalidation). Known ceiling: a read already in flight before the
write commits can repopulate the cache with pre-write data; bounded by the 30 s TTL.

## Next step
Push `feature/unread-messages` and `feature/conversation-list-cache`; open PRs to develop.
