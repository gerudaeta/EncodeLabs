# Chat Inbox

A Telegram conversational inbox built for the EncodeLabs challenge. Telegram delivers text messages through a webhook secured by a shared secret; the .NET API publishes them to RabbitMQ with publisher confirms, a same-host consumer acknowledges manually after idempotent PostgreSQL persistence (retrying transient failures before dead-lettering), and a SignalR hub notifies the Angular inbox in realtime. Operators reply from the inbox; replies are sent through the Telegram Bot API and persisted the same way. Authentication and media messages are not implemented — see [Limitations](#limitations).

## Architecture

- [ADR-001 — PostgreSQL](docs/adr/ADR-001-database.md)
- [ADR-002 — SignalR](docs/adr/ADR-002-realtime.md)
- [ADR-003 — Telegram webhook and RabbitMQ](docs/adr/ADR-003-webhook-rabbitmq.md)
- [ADR-004 — Tailwind CSS](docs/adr/ADR-004-tailwind-css.md)
- [ADR-005 — Conversation list cache](docs/adr/ADR-005-conversation-list-cache.md)
- [C4 context diagram](docs/architecture/c4-context.svg)
- [C4 container diagram](docs/architecture/c4-containers.svg)
- [Working with AI](docs/ai-usage.md)

## Prerequisites

- Docker with Compose v2.
- .NET 10 SDK — only needed to run backend tests outside the container.
- Node.js 24 — only needed to run frontend tests outside the container (the web image builds its own).
- For a credentialed run: a Telegram bot token from [@BotFather](https://t.me/BotFather), a random webhook secret, and an ngrok authtoken ([ngrok dashboard](https://dashboard.ngrok.com)).

## How to fill in `.env`

```sh
cp .env.example .env
```

Every variable in `.env.example`, what it is, and where to get it:

| Variable | What it is | Where to get it |
|---|---|---|
| `TELEGRAM_BOT_TOKEN` | Bot API token identifying your Telegram bot | Create a bot with [@BotFather](https://t.me/BotFather) (`/newbot`); it replies with the token |
| `TELEGRAM_WEBHOOK_SECRET` | Shared secret Telegram echoes back on every webhook call, checked against `X-Telegram-Bot-Api-Secret-Token` | Any random string you generate (letters, digits, `_`, or `-`; 1–256 characters); do not reuse it elsewhere |
| `NGROK_AUTHTOKEN` | Authenticates the ngrok agent so it can open a public HTTPS tunnel to the local API | Your token on the [ngrok dashboard](https://dashboard.ngrok.com) |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | Local PostgreSQL database name and credentials, created fresh by the `postgres` container | Choose any values yourself — not shared with anything outside this stack |
| `RABBITMQ_USER` / `RABBITMQ_PASSWORD` | Local RabbitMQ credentials, created fresh by the `rabbitmq` container | Choose any values yourself |
| `POSTGRES_CONNECTION_STRING` | Connection string the API uses to reach PostgreSQL | Built from the `POSTGRES_*` values above (`Host=postgres;Port=5432;Database=...;Username=...;Password=...`) — keep it in sync if you change them |
| `RABBITMQ_URI` | AMQP URI the API uses to reach RabbitMQ | Built from the `RABBITMQ_*` values above (`amqp://user:pass@rabbitmq:5672/`) — keep it in sync if you change them |

Never commit `.env` or paste its values into logs, tickets, or shared shells.

## How to run

```sh
docker compose up --build
```

Compose starts five services: the API, the Angular shell, PostgreSQL, RabbitMQ, and ngrok. With valid credentials the API discovers the ngrok HTTPS endpoint and registers `/webhooks/telegram` with Telegram automatically — no manual `setWebhook` call is needed.

```sh
docker compose ps
curl -i http://127.0.0.1:8080/health/ready
```

`/health/ready` returns `200` once webhook registration and consumer subscription are both confirmed; otherwise `503` with a redacted failure category.

Once it is ready:

- Inbox UI: <http://localhost:4200>
- API: `127.0.0.1:8080`
- RabbitMQ management UI: `127.0.0.1:15672` (login with `RABBITMQ_USER` / `RABBITMQ_PASSWORD`)

## End-to-end test guide

Manual walkthrough, once `docker compose up --build` reports `/health/ready` as `200`:

1. Open the inbox at <http://localhost:4200>. It starts empty (or shows previously stored conversations).
2. From your own Telegram account, send a text message to your bot.
3. Watch the inbox: within a second or two the conversation appears (or moves to the top) with an unread badge, with no manual refresh — SignalR pushed a `messageStored` event that made the client refetch the list.
4. Open that conversation. The messages load and the unread badge clears immediately; reloading the page confirms the read state persisted (it is stored server-side, not just hidden in the browser).
5. Type a reply in the composer and send it. It is delivered through the Telegram Bot API `sendMessage` call and appears in your Telegram chat with the bot.
6. Optionally verify the same flow with curl instead of the UI:
   ```sh
   curl -i 'http://127.0.0.1:8080/api/conversations?limit=50'
   curl -i -X POST http://127.0.0.1:8080/api/conversations/<id>/read
   curl -i -X POST http://127.0.0.1:8080/api/conversations/<id>/messages \
     -H 'Content-Type: application/json' -d '{"text":"hello from curl"}'
   ```

Automated tests (do not require a bot token or ngrok tunnel — Testcontainers spins up disposable PostgreSQL/RabbitMQ):

```sh
dotnet test src/backend/ChatInbox.slnx
```

```sh
cd src/frontend/chat-inbox-web && npm test -- --watch=false
```

## Endpoints

| Method | Path | Notes |
|---|---|---|
| GET | `/health/ready` | 200 when registration and consumer are ready, else 503 |
| GET | `/api/conversations?limit=&before=` | Keyset pagination, `limit` 1–100 |
| GET | `/api/conversations/{id}/messages?limit=&before=` | Same pagination shape |
| POST | `/api/conversations/{id}/messages` | `{ "text": "..." }`; 400 empty/over 4096 chars, 404 unknown conversation, 502 if Telegram rejects (nothing persisted) |
| POST | `/api/conversations/{id}/read` | Marks all unread inbound messages in the conversation as read; idempotent, 204 on success, 404 unknown conversation |
| POST | `/webhooks/telegram` | Telegram-only; requires the configured secret header |
| — | `/hubs/inbox` (SignalR) | Broadcasts `messageStored` with `{ conversationId }` to all connected clients |

## Security notes

- The API (8080), PostgreSQL (5432), and RabbitMQ (5672, management UI 15672) are bound to `127.0.0.1` only. The ngrok agent API (4040) is not published to the host at all — it is private to the Compose network.
- The web container (4200) is also bound to `127.0.0.1`, because nginx proxies the unauthenticated `/api/` and `/hubs/` routes to the API.
- ngrok's Traffic Policy publicly exposes only `POST /webhooks/telegram`; every other path returns a `403` from ngrok.
- There is no authentication: anyone who can reach the loopback bindings can read conversations, send replies, or receive SignalR notifications. The hub broadcasts to all clients — there is no per-conversation targeting or authorization.

The backend integration suite starts disposable PostgreSQL 17 and RabbitMQ 4.3.6 containers (Testcontainers) and disables only external Telegram/ngrok registration; no bot token or tunnel is required to run it. Domain unit tests are included too (e.g. the read-state rule in `MessageReadStateTests`) — both levels were kept because Testcontainers integration tests are what actually prove idempotent persistence, retry/dead-lettering, and query pagination against a real PostgreSQL/RabbitMQ, which in-memory unit tests over the domain alone cannot exercise.

## Assumptions

Scope decisions made where the challenge brief left something undefined:

- **Single operator, no authentication.** There is one shared inbox with no login and no per-operator identity, for any HTTP route or the SignalR hub. Anyone who can reach the loopback bindings can read conversations, send replies, and receive notifications.
- **Read state is global, not per operator.** "Mark as read" clears the badge for everyone, since there is no concept of separate operators to track it per person.
- **Only text messages are stored.** Other well-formed Telegram update types (photos, documents, voice, edits, etc.) are acknowledged with `200` so Telegram stops retrying, but are ignored and never create a conversation or message.
- **Only inbound messages count as unread.** Outbound replies never increment the unread count; `Message.MarkRead` is a no-op on outbound messages by construction.
- **Conversation identity = Telegram chat.** One Telegram `chat.id` is one conversation; there is no separate concept of grouping or splitting conversations.
- **Read/query routes are loopback-only; only the webhook is public.** The API, PostgreSQL, RabbitMQ, and the web container are bound to `127.0.0.1`; ngrok's Traffic Policy exposes only `POST /webhooks/telegram` publicly (every other path gets a `403` from ngrok). This was treated as an acceptable stand-in for real authentication in a local challenge submission, not as a production posture.
- **Webhook secret validation.** Every webhook call must carry the configured `X-Telegram-Bot-Api-Secret-Token`; requests without it are rejected with `401` before anything is published to RabbitMQ.
- **Idempotency on Telegram `update_id`.** A unique database constraint on `update_id` absorbs webhook redelivery and consumer retries — this is at-least-once delivery with idempotent persistence, not end-to-end exactly-once delivery.
- **Pagination exists even though the brief marks history pagination out of scope.** `GET /api/conversations` and the messages endpoint use keyset pagination (`limit`, `before`) because the read APIs already needed a stable, bounded query shape for correctness (avoiding unbounded result sets); the Angular UI itself only exposes a manual "load more" and there is no infinite-scroll or virtualized history — the out-of-scope item is the UI experience, not the API shape.
- **No responsive design; styling matches the encodelabs.com.ar brand.** The Tailwind CSS v4 setup (see [ADR-004](docs/adr/ADR-004-tailwind-css.md)) reuses color and typography tokens from encodelabs.com.ar for a desktop-oriented single-screen inbox; there is no mobile layout.
- **First conversation page is cached in memory.** The no-cursor, default-size page of `GET /api/conversations` is cached with `HybridCache` and invalidated on every write that affects it (see [ADR-005](docs/adr/ADR-005-conversation-list-cache.md)); this only helps a single API instance, which matches the current deployment.

## Diagnose and recover

Use `docker compose logs api rabbitmq ngrok` and `/health/ready` to inspect failures; avoid logging request URLs containing the bot token or message bodies. RabbitMQ management (`127.0.0.1:15672`) shows `chat-inbox.inbound.q` depth/unacknowledged count and `chat-inbox.dead.q` count. The inbound queue retries transient failures up to five times before dead-lettering; the API stops on terminal broker cancellation and Compose's `restart: on-failure` restarts it.

To recover a dead-lettered item: inspect its `x-death` reason and Telegram update ID, confirm whether it was already committed, then replay it individually to the inbound exchange with its original routing key and persistent delivery — verify the read APIs and queue depth after each replay rather than bulk-replaying.

Stopping the stack does not delete Telegram's webhook or discard its backlog. Before rolling back a deployment, decide whether to restore the previous webhook URL or delete Telegram's webhook explicitly. Never drop PostgreSQL tables or purge RabbitMQ queues automatically — both hold durable state.

## Limitations

- No authentication or authorization on any HTTP route or the SignalR hub.
- Text messages only — no media (photos, documents, voice, etc.).
- Idempotent persistence (unique on Telegram `update_id`) absorbs webhook redelivery and consumer retries, but RabbitMQ itself does not guarantee end-to-end exactly-once delivery.

## Progress trackers

- [odd/tasks/chat-inbox-foundation.md](odd/tasks/chat-inbox-foundation.md)
- [odd/tasks/chat-inbox-inbound.md](odd/tasks/chat-inbox-inbound.md)
- [odd/tasks/chat-inbox-ui-replies.md](odd/tasks/chat-inbox-ui-replies.md)
- [odd/tasks/submission-polish.md](odd/tasks/submission-polish.md)
- [odd/tasks/unread-messages.md](odd/tasks/unread-messages.md)
- [odd/tasks/conversation-list-cache.md](odd/tasks/conversation-list-cache.md)
