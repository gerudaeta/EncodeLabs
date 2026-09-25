# Unread messages

## Objective
Show each contact's unread-message count in the inbox list and mark messages as read when the operator opens the conversation (tech test functional requirements 2 and 4).

## Problem / why
The inbox has no read state: the operator cannot see which conversations have pending messages. This is the only unmet functional requirement of the tech test.

## Scope
- Domain: inbound messages carry a read state (`ReadAt`, nullable); outbound messages are never unread.
- Persistence: EF Core migration for the new column (+ index if useful for the count).
- Read side: conversation list items expose `unreadCount`.
- Write side: `POST /api/conversations/{id}/read` marks all unread inbound messages of that conversation as read (idempotent, 404 for unknown conversation, 204 on success).
- Realtime: after marking read, notify clients through the existing SignalR notifier so other open tabs refresh.
- Frontend: unread badge per contact; opening a conversation calls mark-read and clears the badge; a new inbound message in the currently open conversation is marked read immediately.

## Constraints
- Clean Architecture boundaries as in existing code (Domain / Application / Infrastructure / Api).
- Tests preferably unit tests on the domain rule, plus existing test styles where they already cover the touched component.
- Out of scope: auth, per-operator read state, non-text messages.

## TDD
Enabled (same source as `odd/tasks/chat-inbox-inbound.md`): RED → GREEN → REFACTOR.
Backend runner `dotnet test src/backend/ChatInbox.slnx`; frontend runner `npm test -- --watch=false` in `src/frontend/chat-inbox-web`.

## Tasks
- [x] UNR-01 Domain + persistence: read state on messages, migration, domain unit tests.
- [x] UNR-02 Application + API: `unreadCount` in conversation list, mark-read use case + endpoint + realtime notification, tests.
- [x] UNR-03 Frontend: model, service call, badge, mark-read on open and on realtime inbound for open conversation, tests.
- [ ] UNR-04 Verify: full backend + frontend suites, frontend build, Docker rebuild and manual check.

## Acceptance criteria
- New inbound message increases the contact's unread count without refresh.
- Opening the conversation sets the count to 0 and persists it (survives reload).
- Outbound replies never count as unread.

## Progress / evidence
- UNR-01: added `Message.ReadAt` + `Message.MarkRead(at)` domain rule (unread inbound only,
  idempotent, outbound never unread) with unit tests in `ChatInbox.Tests/MessageReadStateTests.cs`
  (RED confirmed before adding the method, then GREEN). Added filtered index
  `ix_messages_unread` and EF migration `20260925045829_AddMessageReadState` via
  `dotnet tool run dotnet-ef migrations add AddMessageReadState --project src/backend/ChatInbox.Infrastructure --startup-project src/backend/ChatInbox.Api --output-dir Persistence/Migrations`.
  Commit `c3f6de6`.
- UNR-02: added `IReadStateRepository`/`MarkConversationReadUseCase` (Application), `ReadStateRepository`
  (Infrastructure, bulk-loads unread inbound messages and calls `MarkRead`), `POST /api/conversations/{id}/read`
  endpoint (idempotent, 404 unknown conversation, 204 success, reuses the existing `messageStored`
  SignalR event to notify only when something actually changed — avoids a notify-loop with the
  frontend's own re-mark-read on realtime events). Added `UnreadCount` to `ConversationDto`, computed
  via one grouped query in `InboxQueries.ListConversationsAsync`. Unit tests in
  `MarkConversationReadUseCaseTests.cs`; integration tests in `ReadEndpointTests.cs` and an
  `UnreadCountCountsOnlyUnreadInboundMessages` case in `InboxQueryTests.cs`. Commit `a740b73`.
  `dotnet test src/backend/ChatInbox.slnx`: 110 passed, 0 failed.
- UNR-03: added `unreadCount` to the `Conversation` model, `InboxService.markRead()`, an indigo
  unread badge per contact (`data-testid="unread-badge-{id}"`, shown only when count > 0),
  mark-read on `selectConversation` (optimistic local zero + POST) and again on a realtime
  `messageStored` notification for the currently open conversation (self-terminates: the backend
  only re-notifies if it actually marked something, so the second round finds nothing unread and
  stops). `loadConversations()` also forces `unreadCount` to 0 for the open conversation to avoid
  a race with an in-flight mark-read response. Updated existing specs to expect the new mark-read
  request; added badge and mark-read assertions.
  `npm test -- --watch=false`: 19 passed, 0 failed (4 files). `npx ng build`: succeeded.

## Next step
UNR-04: full backend + frontend suites (already green above), frontend build (already green above),
Docker rebuild and manual check — owned by the parent orchestrator.
