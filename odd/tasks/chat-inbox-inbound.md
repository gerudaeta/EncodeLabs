# Chat Inbox Inbound Telegram Slice

## Objective

Deliver the approved second-PR inbound path: authenticated Telegram text webhook, confirmed RabbitMQ intake, idempotent PostgreSQL persistence, and local-only read APIs.

## Problem and why

The merged foundation contains runnable application shells and supporting containers, but no message-processing behavior. This increment makes inbound delivery observable without claiming outbound replies, SignalR, or Angular inbox behavior.

## Authorized scope

- Repository: `/Users/germanudaeta/Documents/EncodeLabs`.
- Branch: `feature/chat-inbox-inbound`, based on merged `develop` at `4037f31`.
- Follow `docs/superpowers/specs/2026-09-24-chat-inbox-inbound-design.md` and `docs/superpowers/plans/2026-09-24-chat-inbox-inbound.md`.
- Implement and verify locally task by task. Do not push, open a PR, merge, deploy, register a real webhook, or use real credentials without separate authorization.

## Constraints and decisions

- One .NET API executable host; inward-only project references; one Telegram bot and text updates only.
- Webhook success follows positive, routable broker confirmation; consumer acknowledgement follows committed or already-committed database work.
- PostgreSQL unique constraints and transaction establish idempotent persistence, not exactly-once delivery.
- Read APIs are unauthenticated local verification surfaces: loopback host port plus ngrok policy restriction.
- No auth, media, edits, outbound replies, SignalR, or Angular inbox in this slice.
- TDD mode: enabled by the applicable `superpowers:test-driven-development` skill and approved implementation plan. Use observed RED → GREEN → REFACTOR for behavior; runner is `dotnet test src/backend/ChatInbox.slnx`, with task-filtered `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~<TestClass>`. Environment/setup failures are not valid RED evidence.
- Approx. 400 authored changed lines per task is advisory only, not a cap. Preserve unrelated work and stage exact files.

## Tasks

- [ ] **INB-01 — Webhook intake:** Authenticated, bounded Telegram text mapping and component tests.
- [ ] **INB-02 — Broker publication:** Durable topology and mandatory publisher confirms/return handling against pinned RabbitMQ.
- [ ] **INB-03 — PostgreSQL store:** Transactional conversation/message model, preview, idempotency, migration, and integration tests.
- [ ] **INB-04 — Consumer:** Same-host manual acknowledgement, bounded retry, and dead-letter behavior.
- [ ] **INB-05 — Read APIs:** Stable keyset queries and local-only conversation/message endpoints.
- [ ] **INB-06 — Registration:** Discover ngrok URL, reconcile Telegram webhook, and expose honest readiness.
- [ ] **INB-07 — Compose security:** Pin images, private ngrok agent access, webhook-only public policy, loopback API, and configuration checks.
- [ ] **INB-08 — End-to-end evidence:** Container integration tests, README, final checks, and honest live-smoke boundary.

## Acceptance criteria

1. Automated tests exercise webhook validation, broker acceptance versus uncertainty, persistence duplicates/rollback, retry/DLQ, queries, and registration decisions.
2. With valid local credentials, plain `docker compose up` can discover ngrok URL and register the webhook automatically; without them, fake/container tests remain runnable.
3. Public ngrok ingress cannot read conversations; localhost can. No secrets or message bodies appear in routine logs.
4. Each verification tier (unit/component, real DB/broker, ngrok/Compose, optional live Telegram) is reported separately.
5. Deferred functionality is not described as working; no remote delivery occurs without authorization.

## Progress and verification

- 2026-09-24: User approved the design and implementation plan. Plan commit `c186624` passed a narrow independent plan review; no source behavior has been implemented.
- Checks pending: all task checks and final branch review.

## Next step

Execute INB-01 with observed RED, GREEN, REFACTOR, then review and update this file and its Engram mirror before INB-02.
