# Chat Inbox Inbound Telegram Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver one inspectable Telegram text-update path from authenticated webhook through confirmed RabbitMQ publication, same-host consumption, idempotent PostgreSQL persistence, and local-only read APIs.

**Architecture:** Keep `ChatInbox.Api` as the sole executable host. Minimal API endpoints call application contracts; infrastructure adapters own Telegram HTTP, RabbitMQ, and PostgreSQL. The webhook acknowledges only confirmed, routable broker acceptance; the consumer acknowledges only committed or already-committed database work. The ngrok agent supplies its URL to an API-hosted registration service.

**Tech Stack:** .NET 10 Minimal APIs and hosted services, RabbitMQ .NET client 7, RabbitMQ 4.3.6 management image, PostgreSQL 17, EF Core 10/Npgsql provider, xUnit, Docker Compose v2, ngrok v3 agent configuration.

**Spec:** `docs/superpowers/specs/2026-09-24-chat-inbox-inbound-design.md`

## Global Constraints

- The PR2 boundary is text-only inbound Telegram, persistence, and local read-only queries; no operator auth, media, edits, outbound replies, SignalR, or Angular inbox behavior.
- `ChatInbox.Api` is the only executable host; `Application` owns use cases/contracts, `Infrastructure` adapters, and `Domain` remains framework-free.
- Require `X-Telegram-Bot-Api-Secret-Token`; request body limit is 64 KiB; text limit is 4,096 Unicode characters; Telegram IDs use 64-bit integers.
- Return `200` only after a mandatory, persistent, routable, positively confirmed broker publish; broker uncertainty returns `5xx`.
- PostgreSQL is authoritative: one transaction covers processed marker, conversation upsert, message insert, and summary update; consumer ack follows commit.
- One durable direct exchange, durable inbound quorum queue, fixed routing key, delivery limit 5, delayed retry `all` with 1,000 ms minimum and 30,000 ms maximum, and durable DLX/DLQ.
- `GET` routes are local verification only: map API host port as `127.0.0.1:8080:8080`; public ngrok traffic policy permits only `POST /webhooks/telegram`.
- Plain `docker compose up` with valid `.env` discovers exactly one HTTPS ngrok URL forwarding to `api:8080` and calls `setWebhook` automatically with `drop_pending_updates=false`, the secret, and `allowed_updates=["message"]`.
- Do not put secrets in source, logs, queued envelopes, snapshots, or automated tests. Live Telegram/ngrok smoke testing is optional and separately reported.
- Approx. 400 authored changed lines per task is a planning heuristic only. Do not truncate correctness or split a coherent behavior merely to fit it.

## Review Focus

1. Authenticated older/unsupported Telegram update must return `200` without enqueue or conversation creation (Task 1 test).
2. An unroutable mandatory publish can receive a broker confirm but must still return `5xx` (Task 2 integration test).
3. Concurrent duplicate `update_id` and crash-after-commit redelivery must persist one message and acknowledge the duplicate (Tasks 3–4 tests).
4. Equal timestamps and late messages must not skip/reorder pages or regress conversation activity (Tasks 3 and 5 tests).
5. Zero/multiple ngrok candidates or a tunnel URL change must not silently register the wrong URL or declare readiness (Task 6 tests).

---

## File map and shared contracts

Existing `src/backend/ChatInbox.slnx` includes four empty .NET 10 projects. Create `src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj` and add it to the solution. Keep each source file focused; proposed ownership below is exclusive to its task. Files not in this map are out of scope unless an executor records why the spec requires them before editing.

| Task | Files owned | Responsibility |
| --- | --- | --- |
| 1 | `ChatInbox.Application/Inbound/InboundTelegramUpdate.cs`, `IInboundPublisher.cs`, `ChatInbox.Api/TelegramWebhook.cs`, `ChatInbox.Api/Program.cs`, tests `WebhookTests.cs` | Transport authentication, validation/mapping, publish response semantics |
| 2 | `ChatInbox.Infrastructure/Messaging/RabbitTopology.cs`, `RabbitInboundPublisher.cs`, `ChatInbox.Infrastructure.csproj`, tests `RabbitPublisherTests.cs` | Durable topology and confirmed mandatory publication |
| 3 | `ChatInbox.Infrastructure/Persistence/InboxDbContext.cs`, `InboxRepository.cs`, `Migrations/*`, application `Inbound/IInboundStore.cs`, tests `InboxStoreTests.cs` | Atomic, idempotent persistence |
| 4 | `ChatInbox.Infrastructure/Messaging/InboundConsumer.cs`, tests `InboundConsumerTests.cs` | Same-host manual-ack consume, retry/DLQ disposition |
| 5 | `ChatInbox.Application/Queries/InboxQueries.cs`, `ChatInbox.Infrastructure/Persistence/InboxQueries.cs`, `ChatInbox.Api/InboxEndpoints.cs`, tests `InboxQueryTests.cs` | Stable keyset read API |
| 6 | `ChatInbox.Infrastructure/Telegram/TelegramRegistration.cs`, `ChatInbox.Api/Readiness.cs`, tests `TelegramRegistrationTests.cs` | ngrok URL selection, Telegram registration/reconciliation, readiness |
| 7 | `docker-compose.yml`, `config/ngrok.yml`, `config/ngrok-policy.yml`, `.env.example`, `ChatInbox.Api/appsettings.json`, tests `ComposeSecurityTests.cs` | Five-service boot and exposure controls |
| 8 | `README.md`, `src/backend/ChatInbox.Tests/Integration/*`, `ChatInbox.slnx` | Integration evidence and operator instructions |

The intended application-facing signatures are:

```csharp
public sealed record InboundTelegramUpdate(int Version, long UpdateId, long ChatId,
    long MessageId, DateTimeOffset SentAt, string Text, long? SenderId,
    string? SenderFirstName, string? SenderLastName, string? ChatTitle);
public interface IInboundPublisher
{
    Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken);
}
public interface IInboundStore
{
    Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken);
}
public enum StoreOutcome { Inserted, AlreadyProcessed }
```

`RabbitInboundPublisher` treats return, nack, timeout, and connection loss as failure even when their outcome is uncertain; the caller must not claim durable persistence. `InboxRepository.StoreAsync` reports `AlreadyProcessed` only after the winning committed transaction is observed. Do not use a process-local duplicate cache.

## Task 1: Authenticated, bounded webhook intake

**Files:** Create the Task 1 files in the file map; modify `ChatInbox.Api/ChatInbox.Api.csproj` only to reference the testable endpoint assembly contract. Create `ChatInbox.Tests/ChatInbox.Tests.csproj` with `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and `Microsoft.AspNetCore.Mvc.Testing` compatible with net10.0; add to solution.

**Interfaces:** Consumes the shared `IInboundPublisher`; produces `TelegramWebhook.MapTelegramWebhook(WebApplication app)` and the version-1 `InboundTelegramUpdate` record. Inject a fake publisher in component tests; no real token.

- [ ] **Step 1 — RED:** Write component tests with `WebApplicationFactory<Program>` replacing `IInboundPublisher` with a recording fake. Send the JSON below with configured test secret `test_secret_123`; assert `200`, exactly one version-1 envelope, `long` IDs, and no credential fields. Repeat with no/wrong secret and assert `401` and zero publishes; use valid JSON `{"update_id":123,"edited_message":{}}` to assert `200` and zero publishes; use 4,097 Unicode scalar values to assert `400`; send malformed JSON, missing `update_id`, wrong content type, and 65,537 bytes to assert `400`, `400`, `415`, and `413` respectively.

```json
{"update_id":2147483648,"message":{"message_id":2147483649,"date":1700000000,"chat":{"id":-2147483650,"title":"Test"},"from":{"id":2147483651,"first_name":"Ada"},"text":"hello"}}
```

- [ ] **Step 2 — verify RED:** Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~WebhookTests`; expect failing assertions because route and mapper are absent, not a fixture/configuration error.
- [ ] **Step 3 — GREEN:** Add strongly validated Telegram options (token and distinct allowed-character 1–256-character secret) without printing values. Compare UTF-8 bytes using `CryptographicOperations.FixedTimeEquals` after equal-length check. Reject before parsing/publishing on absent or wrong secret. Limit body with endpoint request-size metadata and a bounded read; require JSON media type; parse with `JsonDocument`, `TryGetInt64`, and only `message.text`. Count Unicode scalars with `EnumerateRunes().Count()`. Ignore other well-formed update kinds with a safe counter. Route maps application exceptions from broker publish to `503` without logging body/secret; do not turn cancellation into success.
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run the filtered test, then `dotnet test src/backend/ChatInbox.slnx`. Extract only repeated JSON/response helpers in tests; rerun both. Do not log whole `HttpRequest` or `JsonDocument`.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Application/Inbound src/backend/ChatInbox.Api src/backend/ChatInbox.Tests src/backend/ChatInbox.slnx && git commit -m "feat: validate and map Telegram webhooks"`.

## Task 2: Broker topology and confirmed publication

**Files:** Task 2 messaging files and tests; modify `ChatInbox.Api/Program.cs` only for DI and topology startup ordering. Preserve Task 1 route contract.

**Interfaces:** Implement `IInboundPublisher.PublishConfirmedAsync`; expose `RabbitTopology.DeclareAsync(IChannel channel, CancellationToken)` and constants `chat-inbox.inbound`, `chat-inbox.inbound.q`, `chat-inbox.inbound`, `chat-inbox.dead`, `chat-inbox.dead.q`. The publisher owns a dedicated channel or serialized channel access; it must not share an `IChannel` concurrently with the consumer.

- [ ] **Step 1 — RED:** Add a RabbitMQ-container integration test: declare topology, publish one envelope, consume its exact `UpdateId`, and assert persistent delivery. Remove the binding on an isolated exchange in a second test and assert `PublishConfirmedAsync` throws despite a broker confirm. Close broker connection before confirmation in a third test and assert failure/uncertainty, never success. Use a bounded 5-second wait; do not use Telegram credentials.
- [ ] **Step 2 — verify RED:** Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~RabbitPublisherTests`; expect failure for missing publisher/topology. If Docker is unavailable, record the integration test as blocked, not green.
- [ ] **Step 3 — GREEN:** Use RabbitMQ .NET client 7 `CreateChannelOptions` with publisher confirmation tracking enabled and `BasicPublishAsync(exchange, routingKey, mandatory: true, basicProperties, body)` with `DeliveryMode=2`, `ContentType="application/json"`, version header. Await confirmation with a bounded cancellation deadline. Attach `BasicReturn` handling before publication and correlate return to the in-flight message; serialize publication on that channel so correlation is unambiguous. Declare durable direct exchange/queue/binding, inbound `x-queue-type=quorum`, `x-delivery-limit=5`, `x-delayed-retry-type=all`, `x-delayed-retry-min=1000`, `x-delayed-retry-max=30000`, dead-letter exchange/routing key, and durable DLQ. Fail startup on inequivalent preexisting topology rather than silently using it. Keep connection settings out of logs.
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered test and full `dotnet test src/backend/ChatInbox.slnx`; inspect `rabbitmq-diagnostics`/management in the pinned image to verify arguments and binding. Separate serialization from AMQP code if it improves clarity; rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Infrastructure src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: confirm durable RabbitMQ intake"`.

RabbitMQ's official [client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) and [confirm tutorial](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet) describe 7.x `BasicPublishAsync`, mandatory returns, and tracked confirms. The integration test, not a fake, establishes that this exact client/broker combination treats a returned message as failed.

## Task 3: Transactional idempotent PostgreSQL store

**Files:** Task 3 persistence/application files and tests; add EF Core 10, Npgsql EF Core 10, and design-time migration package versions that match the repo's .NET 10 target. `Migrations/*` is generated from the committed model, not handwritten drift.

**Interfaces:** Implement `IInboundStore.StoreAsync`; return `Inserted` or `AlreadyProcessed`. No database type leaks into `Application`.

- [ ] **Step 1 — RED:** Add real PostgreSQL integration tests: store a 64-bit-ID message and assert one processed marker, conversation, and inbound message; run 16 concurrent `StoreAsync` calls with the same `UpdateId` and assert one message and 15 `AlreadyProcessed`; inject a database failure between marker and message write and assert zero rows after rollback; send an older `SentAt` after a newer message and assert `last_message_at` and preview remain newer. Use isolated database/schema per fixture; skip only when Docker unavailable and report the skip.
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~InboxStoreTests`; expected failure is missing store/schema, not unavailable Docker.
- [ ] **Step 3 — GREEN:** Map `conversations`, `messages`, `processed_updates` with UUID PKs, bigint Telegram IDs, required text/direction/timestamps, unique chat ID, unique update ID, unique `(conversation_id,telegram_message_id)`, FK, and indexes `(last_message_at DESC,id DESC)` and `(conversation_id,sent_at,id)`. In one transaction insert marker, upsert conversation by `telegram_chat_id`, insert message, update display/summary only if `(SentAt, message ID)` is later than current summary. On PostgreSQL unique violation for `processed_updates.update_id`, roll back and query that marker in a fresh transaction; return duplicate success only if committed marker exists. Re-throw any other unique/DB failure. Generate migration with `dotnet ef migrations add InitialInbox --project src/backend/ChatInbox.Infrastructure --startup-project src/backend/ChatInbox.Api --output-dir Persistence/Migrations`; apply migrations at startup before consumer/readiness, fail visibly if unavailable.
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered and full solution tests, then `dotnet ef migrations script --idempotent --project src/backend/ChatInbox.Infrastructure --startup-project src/backend/ChatInbox.Api` to inspect SQL. Refactor transaction helper only after green and rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Application/Inbound/IInboundStore.cs src/backend/ChatInbox.Infrastructure/Persistence src/backend/ChatInbox.Infrastructure/ChatInbox.Infrastructure.csproj src/backend/ChatInbox.Tests src/backend/ChatInbox.Api/Program.cs && git commit -m "feat: persist Telegram updates idempotently"`.

Do not claim cross-process exactly-once. The unique constraint and post-conflict committed-marker check, not an in-memory lock, establish the duplicate outcome.

## Task 4: Same-host consumer with bounded retry and DLQ

**Files:** Task 4 consumer/test files; modify `ChatInbox.Api/Program.cs` only for hosted-service registration and readiness integration.

**Interfaces:** `InboundConsumer : BackgroundService` consumes version-1 envelopes and calls `IInboundStore.StoreAsync`. It owns a separate consumer channel and `BasicQosAsync(prefetchCount: 1)`; one scoped store transaction per delivery.

- [ ] **Step 1 — RED:** With pinned RabbitMQ and PostgreSQL containers, publish a valid envelope, wait until stored, and assert queue delivery is acknowledged only after commit. Publish the same update again and assert one message and drained queue. Force a transient store failure, assert no positive ack and subsequent delivery; restore DB before fifth failure and assert one store. Publish malformed version/JSON and assert it appears in DLQ. Force five `basic.reject(requeue=true)` failures and assert DLQ presence with `x-death`/delivery diagnostic headers; this last test is a required pinned-image contract check.
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~InboundConsumerTests`; expected missing consumer/disposition behavior. If the image does not count rejection toward limit, stop and revise the design with the human; do not replace bounded retries with an unbounded loop.
- [ ] **Step 3 — GREEN:** Start consumer after migration/topology readiness; deserialize strictly by `Version`. Use manual acknowledgements: `BasicAckAsync` only after `Inserted` or `AlreadyProcessed` returns; `BasicRejectAsync(requeue: true)` on transient persistence failures; `BasicRejectAsync(requeue: false)` on malformed/unsupported envelopes. Prefetch 1, one consumer channel, cancellation-aware shutdown. Log `UpdateId` and failure category only; expose processing-failure counter. If a channel closes before ack, allow RabbitMQ to redeliver; never issue a synthetic success. Do not republish from consumer.
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered and full solution tests. On actual `rabbitmq:4.3.6-management`, observe retry delay, delivery count, and dead-lettering; report exact result separately from unit tests. Refactor disposition classification only after green, then rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Infrastructure/Messaging/InboundConsumer.cs src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: consume inbound updates with bounded retries"`.

Official [RabbitMQ 4.3 quorum queue docs](https://www.rabbitmq.com/docs/quorum-queues) state delayed retry `all` and that AMQP 0.9.1 `basic.reject`, unlike `basic.nack`, increments delivery count; image-level testing remains mandatory.

## Task 5: Stable local read queries

**Files:** Task 5 query/endpoint/test files; modify `ChatInbox.Api/Program.cs` only to map routes.

**Interfaces:** `IInboxQueries.ListConversationsAsync(int limit, PageCursor? before, CancellationToken)` and `ListMessagesAsync(Guid conversationId, int limit, PageCursor? before, CancellationToken)` return `Page<T>` (`IReadOnlyList<T> Items`, `string? NextCursor`). `PageCursor` is `(DateTimeOffset Timestamp, Guid Id)` encoded as opaque base64url JSON with version 1; parser returns a typed invalid result, never throws to endpoint. Conversation DTO has `Id`, `TelegramChatId`, `DisplayName`, `LastMessageAt`, `LastMessagePreview`; message DTO has `Id`, `TelegramMessageId`, `Direction`, `Text`, `SentAt`.

- [ ] **Step 1 — RED:** Seed two conversations with equal `last_message_at` and three messages with equal `sent_at`; request `limit=1` pages until exhausted and assert no duplicate/skip, newest conversations first, newest message page selected but each returned page chronological. Assert default 50, accepted 1/100, rejected 0/101/non-numeric and tampered cursor (`400`), unknown conversation (`404`). Include late message test showing conversation remains ordered by newest message. Assert no GET mutates row counts.
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~InboxQueryTests`; expected absent query/routes failure.
- [ ] **Step 3 — GREEN:** Query keyset `(timestamp,id) < (@timestamp,@id)` in descending selection order with `limit+1`, stable GUID tie-break, and indexes from Task 3. For messages reverse the selected page only when formatting the response. Encode/decode cursors with version, timestamp, id and strict length/format bounds. Reject invalid limits/cursors, and check conversation existence before empty-page response. No write-side calls from endpoints.
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered and full tests; inspect PostgreSQL query plan on seeded data for the two indexes. Extract cursor parser only if clarity improves; rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Application/Queries src/backend/ChatInbox.Infrastructure/Persistence/InboxQueries.cs src/backend/ChatInbox.Api/InboxEndpoints.cs src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: expose stable local inbox queries"`.

## Task 6: Automatic, reconciled Telegram registration

**Files:** Task 6 registration/readiness/test files; modify `ChatInbox.Api/Program.cs` for typed `HttpClient`, options, and hosted service.

**Interfaces:** `NgrokTunnelSelector.SelectHttpsApiTunnel(JsonElement tunnels) : Uri` accepts exactly one HTTPS `public_url` whose tunnel config forwards to `http://api:8080`; zero or multiple matches is a typed failure. `TelegramRegistration : BackgroundService` queries `http://ngrok:4040/api/tunnels`, posts TLS-verified `https://api.telegram.org/bot{token}/setWebhook`, and queries `getWebhookInfo`; `IRegistrationStatus` exposes ready, current URL, last error category, pending count, and last Telegram delivery error without secrets.

- [ ] **Step 1 — RED:** Unit-test zero, one, and two matching HTTPS tunnels; reject a tunnel forwarding to another service. Fake `HttpMessageHandler`: verify every startup calls `setWebhook` even when `getWebhookInfo.url` already matches, with `secret_token`, `allowed_updates=["message"]`, `drop_pending_updates=false`; simulate transient failures and recovery with bounded backoff; change URL during running service and assert re-registration/not-ready during mismatch. Assert logs/status never contain bot token or secret.
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~TelegramRegistrationTests`; expected missing selector/service behavior.
- [ ] **Step 3 — GREEN:** Wait until route, broker intake, migration, and consumer readiness exist. Poll ngrok agent API with a short timeout; select one URL; POST Telegram registration on every startup and on URL change, then observe `getWebhookInfo` URL/pending/errors. Bounded exponential retry (1, 2, 4, 8, 16, 30 seconds) continues while host runs; expose unready with error category after failed attempt, not a false-green health signal. Never call `deleteWebhook` on shutdown. Keep a dedicated TLS-validating client; redact token-bearing request URI from logs/diagnostics.
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered/full tests. With an actual ngrok agent image and private Compose network, verify config/API response shape and HTTPS URL selection; mark this integration check blocked if credentials unavailable. Refactor status state transitions only after green; rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Infrastructure/Telegram src/backend/ChatInbox.Api/Readiness.cs src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: reconcile Telegram webhook registration"`.

## Task 7: Five-service Compose and transport exposure

**Files:** Task 7 configuration and tests. No secret-bearing `.env` is committed.

**Interfaces:** Compose keeps `api`, `web`, `postgres`, `rabbitmq`, `ngrok`. API receives validated `Telegram__BotToken`, `Telegram__WebhookSecret`, `RabbitMq__*`, and `ConnectionStrings__Inbox`; ngrok agent API is private at `ngrok:4040`. Expose local status/metrics on the API loopback mapping only; RabbitMQ management supplies queue/DLQ depths locally.

- [ ] **Step 1 — RED:** Write configuration contract tests that parse checked-in Compose/config: API maps only `127.0.0.1:8080:8080`; port 4040 is absent from host mappings; ngrok forwards `api:8080`; policy denies all except `POST /webhooks/telegram`; image is `rabbitmq:4.3.6-management`; `.env.example` has placeholders for both Telegram secrets. Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~ComposeSecurityTests` and expect missing settings failures.
- [ ] **Step 2 — GREEN:** Update Compose to pin RabbitMQ, loopback-bind API, pass private environment variables, mount a checked-in ngrok v3 config, and apply ngrok Traffic Policy on the public endpoint. Configure `agent.web_addr` so `ngrok:4040` is reachable on the private Compose network, and `agent.web_allow_hosts` narrowly for `ngrok`; do not publish 4040. Use the following candidate config, with `NGROK_AUTHTOKEN` coming from the container environment rather than the file; run `ngrok config check` in the selected image and correct only documented syntax. The policy denies all methods/paths other than the webhook POST. Update `.env.example` and API options defaults without values. If the selected image rejects this config or policy, stop and revise design rather than leave public GET routes reachable.

```yaml
# config/ngrok.yml
version: 3
agent:
  web_addr: 0.0.0.0:4040
  web_allow_hosts:
    - ngrok
endpoints:
  - name: telegram-webhook
    traffic_policy_file: /etc/ngrok/ngrok-policy.yml
    upstream:
      url: http://api:8080
```

```yaml
# config/ngrok-policy.yml
on_http_request:
  - expressions:
      - "req.method != 'POST' || req.url.path != '/webhooks/telegram'"
    actions:
      - type: deny
```
- [ ] **Step 3 — verify GREEN:** Run `docker compose config --quiet` with a temporary placeholder-only env file supplied via `--env-file`; run filtered/full tests. With valid local credentials only, inspect `docker compose ps`, ngrok policy behavior (`POST` allowed, `GET /api/conversations` denied), API loopback and LAN reachability, and private-only `ngrok:4040` accessibility. Report credentialed checks separately; do not display Compose rendered secrets.
- [ ] **Step 4 — commit:** `git add docker-compose.yml config/ngrok.yml config/ngrok-policy.yml .env.example src/backend/ChatInbox.Api/appsettings.json src/backend/ChatInbox.Tests && git commit -m "chore: secure local inbound stack exposure"`.

Official [ngrok v3 agent config](https://ngrok.com/docs/agent/config/v3) is the authority for `agent.web_addr` and `agent.web_allow_hosts`; selected-image behavior and Traffic Policy admission need actual integration proof.

## Task 8: End-to-end evidence and operator guide

**Files:** Task 8 integration tests, `README.md`, and solution membership only. No Angular source changes.

**Interfaces:** Tests use local disposable PostgreSQL/RabbitMQ containers and the actual API host. Optional live smoke requires explicit local credentials, never CI secrets in code or snapshots.

- [ ] **Step 1 — RED:** Add an integration test that sends an authenticated webhook to the test API with fake Telegram/ngrok registration, waits for one persisted message, queries both GET routes, repeats the update, restarts the consumer host, and asserts one message. Add broker-unavailable `5xx` and DB-outage retry/DLQ tests. Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~Integration`; expected failure before harness wiring. Keep a bounded timeout and diagnostic failure message without payload text.
- [ ] **Step 2 — GREEN:** Wire disposable containers and test host so production DI/topology/migrations/consumer run; replace only external Telegram registration and secrets. Make the tests deterministic through queue-state and DB-state conditions, not arbitrary sleeps. Write README commands for `.env` setup, `docker compose up --build`, local queries, readiness and broker diagnostics, optional `getWebhookInfo` verification, secret-safe troubleshooting, replay-from-DLQ procedure after fixing cause, and shutdown behavior. State that public GETs, outbound, SignalR, and Angular inbox remain out of scope.
- [ ] **Step 3 — verify GREEN and REFACTOR:** Run `dotnet test src/backend/ChatInbox.slnx`, `dotnet build src/backend/ChatInbox.slnx --no-restore`, `docker compose config --quiet` with placeholder-only env file, and `git diff --check`. Credentialed smoke, when available: `docker compose up --build`, compare `getWebhookInfo.url` with discovered HTTPS URL plus path, send a real Telegram message, query local GETs, exercise missing-secret rejection, and confirm public GET/LAN denial. Record each command/result; no credentialed smoke means explicitly **not verified live**. Do not claim an image health check proves the Telegram path.
- [ ] **Step 4 — commit:** `git add README.md src/backend/ChatInbox.Tests/Integration src/backend/ChatInbox.slnx && git commit -m "test: cover inbound Telegram vertical slice"`.

## Delivery, rollback, and evidence boundary

Each task is independently reviewable and commits only its owned files. Never run multiple writers against `Program.cs` at once. Source-mutating formatters run before final verification/review; do not change candidate bytes afterward without rerunning checks. The final branch review must inspect secret exposure, AMQP return/confirm races, DB duplicate races, cursor stability, and public routing. Do not push, open a PR, deploy, or enable a real webhook merely because tests pass; those are separate delivery actions.

Rollback the application change by reverting PR2 and restoring the prior Compose image/config; **do not** automatically drop PostgreSQL tables, purge RabbitMQ queues/DLQ, or delete Telegram's webhook, since those hold durable state/backlog. If a real registration has happened, explicitly coordinate the prior webhook URL or `deleteWebhook` with the operator before shutting down the replacement. Database migration compatibility and queue topology changes require a rehearsed local rollback on a copied data set before production use. A failed live smoke or DLQ entry is not persisted success.

Evidence tiers: (1) unit/component fakes, (2) real PostgreSQL/RabbitMQ container integration including 4.3 retry/return semantics, (3) ngrok image/config and Compose security checks, (4) optional credentialed Telegram delivery. Report pass/fail/blocked separately and never promote a lower tier into a higher one.

## Plan self-review

- Spec coverage: webhook limits/auth/unsupported updates → Task 1; confirmed publication/topology → Task 2; idempotent transaction → Task 3; consumer retry/DLQ → Task 4; pagination → Task 5; registration/reconciliation → Task 6; private exposure/config → Task 7; full-path evidence and README → Task 8.
- The five Review Focus conditions each have an owning RED test. No source implementation is authorized by this document alone; review the plan before execution.
- External uncertainty remains deliberately visible: RabbitMQ 4.3.6 AMQP `basic.reject` retry/dead-letter behavior, the selected ngrok image's config/Traffic Policy syntax, and whether its local API exposes a v3 endpoint through `/api/tunnels` with `public_url`/upstream details require live container checks. If either provider contract fails, stop and revise the approved design instead of silently weakening it.
