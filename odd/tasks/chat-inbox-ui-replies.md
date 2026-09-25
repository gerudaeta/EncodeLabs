# Chat Inbox UI, Replies and Realtime

## Objective

Finish the remaining challenge flows: an operator inbox in Angular, outgoing Telegram replies, and SignalR notifications, on top of the verified inbound slice (PR #2).

## Problem and why

The inbound flow persists Telegram messages, but nothing is visible to the operator, the operator cannot answer, and the browser does not learn about new messages. These are required by the challenge (see `docs/superpowers/specs/2026-09-24-project-foundation-design.md`, "Deferred integration work") and ADR-002.

## Scope

- Angular inbox: conversation list, message thread, reply composer, consuming the existing read APIs.
- Same-origin access: nginx proxies `/api` and `/hubs` to `api:8080` (WebSocket upgrade for `/hubs`); `ng serve` uses an equivalent dev proxy. No CORS.
- `POST /api/conversations/{id}/messages` sends the text through Telegram `sendMessage`, then persists it as `direction = "outbound"`.
- SignalR hub `/hubs/inbox` notifies after a committed inbound message and after a persisted reply. The client reconciles by refetching; persisted data stays authoritative.
- Bind the web container to `127.0.0.1:4200`, because it now proxies unauthenticated API routes.

## Constraints

- Branch `feature/chat-inbox-ui-replies`, stacked on `feature/chat-inbox-inbound` (PR #2, open against `develop`).
- No authentication, media, NgRx, component library, or SSR (out of challenge scope for this PR).
- The ngrok policy stays webhook-only; `/api` and `/hubs` must never be publicly reachable.
- Keep the Clean Architecture boundaries from the foundation spec; thin Minimal API handlers; no MediatR/AutoMapper/repository abstractions for their own sake.
- Artifacts in English. Conventional commits, no AI attribution.
- TDD enabled (same source as `odd/tasks/chat-inbox-inbound.md`): observed RED → GREEN → REFACTOR. Backend runner `dotnet test src/backend/ChatInbox.slnx`; frontend runner `npm test` in `src/frontend/chat-inbox-web` (Vitest).

## Tasks

- [x] **UI-01 — Inbox UI:** Conversation list with pagination cursor, message thread per conversation, loading/empty/error states; nginx and dev proxy; web bound to loopback. Component/service tests.
- [x] **UI-02 — Outgoing replies:** Application use case + Telegram `sendMessage` adapter; POST endpoint with validation (non-empty, max 4096 chars, unknown conversation → 404, Telegram failure → 502 and nothing persisted); updates conversation preview/last activity; composer in the thread. Backend tests with fake Telegram + real PostgreSQL; frontend tests.
- [x] **UI-03 — Realtime:** SignalR hub, notifications from the consumer after commit and from the reply use case; Angular client with automatic reconnect that refetches list/thread on notification. Backend hub test; frontend tests with a fake connection.
- [ ] **UI-04 — Close:** README and ADR-002 status update, full backend + frontend suites, Compose config check, live smoke with the running stack.

## Acceptance criteria

- The operator sees conversations and messages at `http://localhost:4200`, replies, and the reply arrives in Telegram.
- A new Telegram message appears in the open inbox without manual refresh.
- Public ngrok URL still denies everything except `POST /webhooks/telegram`; LAN cannot reach 4200 or 8080.

## Progress

- 2026-09-25 Created. Branch created from `feature/chat-inbox-inbound` at `58120aa`.
- 2026-09-25 UI-01 done. Added `InboxService`, `InboxComponent` (signal-based conversation list +
  thread with loading/empty/error states, cursor "load more"), and `InboxRealtimeService` (scaffolded
  for UI-03, currently a thin SignalR wrapper with no server yet). Wired `provideHttpClient()`,
  nginx `/api/` and `/hubs/` reverse proxy (with WebSocket upgrade headers) to `api:8080`,
  `proxy.conf.json` for `ng serve`, web bound to `127.0.0.1:4200` and depending on `api` in
  docker-compose.yml. RED observed for `inbox-realtime.service.spec.ts` (3 real assertion/timeout
  failures against a stub `connect()`) and `inbox.component.spec.ts` (11 real assertion/runtime
  failures against a stub component) before implementing; GREEN after. `inbox.service.spec.ts` and
  the new Compose security test were written directly against the implementation (mechanical
  glue/config, not RED-first). Verification: `npm test -- --watch=false` → 4 files / 17 tests passed;
  `npm run build` → succeeded; `dotnet test --filter ComposeSecurityTests` → 6 passed.

- 2026-09-25 UI-02 done. Added `SendReplyUseCase` (Application) calling `IReplySender` (Telegram
  `sendMessage`, implemented by `TelegramReplySender`) before `IReplyRepository` (implemented by
  `ReplyRepository`) persists the outbound message and advances the conversation summary using the
  same race-safe CASE update as inbound storage; migration `AllowNullOutboundUpdateId` makes
  `messages.telegram_update_id` nullable since replies have no Telegram update id. Endpoint
  `POST /api/conversations/{id}/messages` validates (trim, non-empty, <=4096 scalars → 400
  ProblemDetails), maps `ConversationNotFoundException` → 404 and `TelegramSendFailedException` →
  502. `IInboxNotifier` introduced now (Application abstraction) with a temporary `NoopInboxNotifier`
  until UI-03 wires the real SignalR-backed one. Composer UI/tests were already added with UI-01.
  RED observed for `SendReplyUseCaseTests` (4 real `NotImplementedException`/exception-type failures
  against a stub use case) before implementing; GREEN after. `ReplyEndpointTests` (Postgres +
  fake `IReplySender`) written and passing directly against the finished endpoint (integration
  wiring, not RED-first). Verification: `dotnet test` → 95 passed (full suite, no regressions).

- 2026-09-25 UI-03 done. Added SignalR hub `InboxHub` at `/hubs/inbox` (mapped unconditionally) and
  `SignalRInboxNotifier` (Api-layer `IInboxNotifier` implementation, replacing the temporary
  `NoopInboxNotifier`), broadcasting `messageStored` with `{ conversationId }` to all clients.
  `InboundConsumer` now takes an optional `IInboxNotifier` and notifies once per newly-committed
  inbound message (looked up by `telegram_chat_id`, never for a redelivered duplicate); notify
  failures are logged and swallowed so persisted data stays authoritative and the message still
  acks. `SendReplyUseCase` (from UI-02) already notified after a persisted reply. Frontend
  `InboxRealtimeService`/wiring in `InboxComponent` was already added in UI-01. RED observed for
  `SignalRInboxNotifierTests` (NotImplementedException against a stub notifier) and
  `InboundConsumerNotificationTests` (real 10s timeout waiting for a notification that never came)
  before implementing; GREEN after. `InboxHubTests` (real `HubConnection` over the TestServer,
  asserting a POST reply broadcasts `messageStored` with the right conversation id) passed directly
  against the finished wiring. Verification: `dotnet test` → 98 passed; `npm test -- --watch=false`
  → 4 files / 17 tests; `npm run build` → succeeded; Compose config check with dummy env vars →
  exit 0.

## Next step

UI-04 (out of scope for this delegation; not started).
