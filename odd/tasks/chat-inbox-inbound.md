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

- [x] **INB-01 — Webhook intake:** Authenticated, bounded Telegram text mapping and component tests.
- [x] **INB-02 — Broker publication:** Durable topology and mandatory publisher confirms/return handling against pinned RabbitMQ.
- [x] **INB-03 — PostgreSQL store:** Transactional conversation/message model, preview, idempotency, migration, and integration tests.
- [x] **INB-04 — Consumer:** Same-host manual acknowledgement, bounded retry, and dead-letter behavior.
- [x] **INB-05 — Read APIs:** Stable keyset queries and conversation/message endpoints; local-only exposure remains an INB-07 requirement.
- [x] **INB-06 — Registration:** Discover ngrok URL, reconcile Telegram webhook, and expose honest readiness; actual agent/image integration remains INB-07.
- [x] **INB-07 — Compose security:** Pin images, private ngrok agent access, webhook-only public policy, loopback API, and static configuration checks; live policy proof remains pending.
- [x] **INB-08 — End-to-end evidence:** Container integration tests, README, final checks, and honest live-smoke boundary.

## Acceptance criteria

1. Automated tests exercise webhook validation, broker acceptance versus uncertainty, persistence duplicates/rollback, retry/DLQ, queries, and registration decisions.
2. With valid local credentials, plain `docker compose up` can discover ngrok URL and register the webhook automatically; without them, fake/container tests remain runnable.
3. Public ngrok ingress cannot read conversations; localhost can. No secrets or message bodies appear in routine logs.
4. Each verification tier (unit/component, real DB/broker, ngrok/Compose, optional live Telegram) is reported separately.
5. Deferred functionality is not described as working; no remote delivery occurs without authorization.

## Progress and verification

- 2026-09-24: User approved the design and implementation plan. Plan commit `c186624` passed a narrow independent plan review before source implementation began.
- 2026-09-24 INB-01: Commits `3b6ef08` and `3bf0d11` added the webhook endpoint, envelope contract, validated options, and component tests. TDD RED was observed for absent route and malformed-message behavior. The implementer reported full solution tests 21/21; the parent independently reran scoped webhook tests 21/21. Independent static review approved after fixing malformed present `message` payloads to return `400` while valid non-text messages remain ignored with `200`. Until INB-02 wires a broker, the placeholder publisher returns `503`; no Telegram/RabbitMQ live behavior is claimed.
- 2026-09-24 INB-02: Commits `16635ed`, `6e3f9b9`, and `4244119` added durable topology and a serialized publisher with mandatory returns and positive confirms, plus recovery/disposal fixes and self-contained tests. A clean cancellation RED was observed after pinned-broker setup. The implementer reported 7/7 broker tests and 28/28 full-solution tests; the parent independently reran the broker group 7/7 and the formerly order-dependent cancellation test alone 1/1. Independent static review approved after correcting publisher recovery, host ownership, and test isolation. This proves local broker publication, not retry/DLQ disposition, database persistence, or live Telegram delivery.
- 2026-09-24 INB-03: Commits `dc3f355` and `a667875` added EF Core/PostgreSQL schema, idempotent transaction, migration, and real PostgreSQL tests. TDD RED exposed duplicate-scope, null-summary, and stale display-name behavior; fixes now preserve timestamp, preview, and display ordering. The implementer reported 9/9 store tests and 37/37 full-solution tests; the parent independently reran the store group 9/9. Generated migration SQL was inspected; scoped static review approved. Compose database wiring remains INB-07; consumer acknowledgements and live Telegram remain unverified.
- 2026-09-24 INB-04: Commits `2f4d789` and `1626c08` added a same-host consumer with manual ack after committed or duplicate store outcome, bounded RabbitMQ 4.3.6 retry, and DLQ handling. TDD RED was observed before consumer behavior and for broker cancellation. The parent independently reran pinned RabbitMQ/PostgreSQL consumer tests 7/7; the implementer reported full solution 44/44. Tests assert six deliveries under limit 5 and `delivery_limit` dead-letter reason. Scoped static review approved fail-stop on terminal broker cancellation; restart is required rather than automatic recovery. No live Telegram/ngrok flow is claimed.
- 2026-09-24 INB-05: Commits `aef995e` and `4179d72` added keyset conversation/message GET routes and real PostgreSQL tests. TDD RED was observed for missing routes and four malformed-Unicode cursor cases that returned 500 before correction. The parent independently reran focused tests 10/10; the implementer reported full solution 54/54. Scoped static review approved. **Do not run exposed Compose or deliver this branch yet:** unauthenticated GETs remain unsafe until INB-07 binds API to loopback and restricts ngrok to the webhook POST.
- 2026-09-24 INB-06: Commits `9770daa` and `8e299e8` added fake-backed ngrok URL selection, Telegram webhook reconciliation, redacted status/readiness, and actual consumer-subscription and HTTP-listener startup gates. Registration is opt-in/default-off outside Compose; INB-07 must set `Telegram__RegistrationEnabled=true`. TDD RED identified token-colon URL parsing, false setWebhook response, listener order, and terminal/subscription readiness race. The implementer reported full solution 74/74; the parent independently reran registration/readiness tests 19/19. Independent scoped re-review approved both lifecycle corrections. First full-suite run had two consumer timeouts under concurrent containers; isolated and subsequent full reruns passed, but root cause is not established. No real ngrok, Telegram, or exposed Compose run occurred; INB-07 remains the exposure gate.
- 2026-09-24 INB-07: Commits `4ef1ee4` and `fb8612a` loopback-bound the API, restricted the ngrok public endpoint with a webhook-only deny policy, kept agent port 4040 unpublished, pinned RabbitMQ/ngrok images, enabled registration in Compose, and aligned discovery to the documented v3 `/api/endpoints` contract. RED configuration tests and eight legacy-API contract failures preceded fixes. The implementer reported full solution 80/80; the parent independently reran focused registration/Compose tests 23/23. `docker compose config --quiet`, matching host-ngrok 3.39.11 `config check`, and scoped static review passed. **Exposure remains unverified:** selected ngrok Docker image was not locally cached, so no image-specific agent response, public policy behavior, LAN denial, or live Telegram test was run. Do not start an exposed stack or assert that GETs are publicly denied until those checks are authorized and pass. Task 8 updates stale `/api/tunnels` documentation; internal type names retaining `Tunnel` are cosmetic follow-up only.
- 2026-09-24 INB-08: Commit `3b90691` added production-host/Testcontainers end-to-end tests with real PostgreSQL 17 and RabbitMQ 4.3.6, covering webhook-to-GET visibility, duplicate replay after host restart, broker fail-closed behavior, and database outage to bounded retry/DLQ. External ngrok/Telegram registration is disabled only in this test host. The implementer reported full solution 83/83 and build with zero warnings/errors; the parent independently reran focused end-to-end tests 3/3. Placeholder-only Compose validation, diff check, and final independent static branch review passed. README and approved documents now use the v3 `/api/endpoints` contract and explain local diagnostics, rollback, and live security gate. No ngrok Docker image, public tunnel, real credentials, or Telegram delivery was exercised.
- 2026-09-24 Image check: Pulled `ngrok/ngrok:3.39.11-alpine` (digest `sha256:187e588f6c4efe3b29cd3eea9fcd768a1afa7342319e3cee9aeb5af6a9cf64fd`, `ngrok version 3.39.11`) with user authorization. `config check` against the checked-in `config/ngrok.yml` and policy passed inside the image with `--network none`. No authtoken, tunnel, or live policy behavior was exercised.
- 2026-09-25 Live verification: With user authorization, the five-service Compose stack ran with real ngrok and Telegram credentials. The API selected the single HTTPS `/api/endpoints` entry after one startup retry (`ngrok_endpoint_selection` while the agent was starting), registered the webhook, and `/health/ready` reported `ready: true` with no errors. Public probes: GET `/health/ready`, `/api/conversations`, `/webhooks/telegram` and POST `/api/conversations` returned ngrok 403. POST `/webhooks/telegram` without the secret returned 401 from Kestrel. LAN probes against the host IP: 8080, 5432, 5672, 15672, 4040 were closed. 4200 (static frontend from PR1) is published on all interfaces by design. A real Telegram text message was persisted and returned by local GET `/api/conversations`.
- Checks pending: remote delivery authorization (push and PR against `develop`).

## Next step

Push `feature/chat-inbox-inbound` and open PR #2 against `develop` after explicit remote-delivery authorization.
