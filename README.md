# Chat Inbox

A Telegram conversational inbox built for the EncodeLabs challenge. Telegram delivers text messages through a webhook secured by a shared secret; the .NET API publishes them to RabbitMQ with publisher confirms, a same-host consumer acknowledges manually after idempotent PostgreSQL persistence (retrying transient failures before dead-lettering), and a SignalR hub notifies the Angular inbox in realtime. Operators reply from the inbox; replies are sent through the Telegram Bot API and persisted the same way. Authentication and media messages are not implemented — see [Limitations](#limitations).

## Architecture

- [ADR-001 — PostgreSQL](docs/adr/ADR-001-database.md)
- [ADR-002 — SignalR](docs/adr/ADR-002-realtime.md)
- [ADR-003 — Telegram webhook and RabbitMQ](docs/adr/ADR-003-webhook-rabbitmq.md)
- [C4 context diagram](docs/architecture/c4-context.svg)
- [C4 container diagram](docs/architecture/c4-containers.svg)

## Prerequisites

- Docker with Compose v2.
- .NET 10 SDK — only needed to run backend tests outside the container.
- Node.js 24 — only needed to run frontend tests outside the container (the web image builds its own).
- For a credentialed run: a Telegram bot token from [@BotFather](https://t.me/BotFather), a random webhook secret, and an ngrok authtoken ([ngrok dashboard](https://dashboard.ngrok.com)).

## Quick start

```sh
cp .env.example .env
```

Fill in `.env`:

- `NGROK_AUTHTOKEN` — from your ngrok dashboard.
- `TELEGRAM_BOT_TOKEN` — from BotFather.
- `TELEGRAM_WEBHOOK_SECRET` — any random string Telegram accepts as a header value (letters, digits, `_`, or `-`; 1–256 characters). Do not reuse it elsewhere.
- `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` and `RABBITMQ_USER` / `RABBITMQ_PASSWORD` — local credentials for the Compose services.
- `POSTGRES_CONNECTION_STRING` / `RABBITMQ_URI` — must reference the same host/credentials as the values above.

Never commit `.env` or paste its values into logs, tickets, or shared shells.

```sh
docker compose up --build
```

Compose starts the API, Angular shell, PostgreSQL, RabbitMQ, and ngrok. With valid credentials the API discovers the ngrok HTTPS endpoint and registers `/webhooks/telegram` with Telegram automatically.

```sh
docker compose ps
curl -i http://127.0.0.1:8080/health/ready
```

`/health/ready` returns `200` once webhook registration and consumer subscription are both confirmed; otherwise `503` with a redacted failure category.

## Exercise each flow

1. **Inbound message:** send a text message to your bot in Telegram. Confirm it was stored:
   ```sh
   curl -i 'http://127.0.0.1:8080/api/conversations?limit=50'
   ```
2. **Inbox UI:** open `http://localhost:4200/inbox` — the conversation and message appear.
3. **Reply:** open the conversation thread in the UI and send a reply. It is delivered through Telegram `sendMessage` and persisted as an outbound message (equivalent to `POST /api/conversations/{id}/messages` with `{ "text": "..." }`).
4. **Realtime:** with the inbox open, send another Telegram message — it appears without a manual refresh, via the SignalR hub at `/hubs/inbox` (`messageStored` event).

## Endpoints

| Method | Path | Notes |
|---|---|---|
| GET | `/health/ready` | 200 when registration and consumer are ready, else 503 |
| GET | `/api/conversations?limit=&before=` | Keyset pagination, `limit` 1–100 |
| GET | `/api/conversations/{id}/messages?limit=&before=` | Same pagination shape |
| POST | `/api/conversations/{id}/messages` | `{ "text": "..." }`; 400 empty/over 4096 chars, 404 unknown conversation, 502 if Telegram rejects (nothing persisted) |
| POST | `/webhooks/telegram` | Telegram-only; requires the configured secret header |
| — | `/hubs/inbox` (SignalR) | Broadcasts `messageStored` with `{ conversationId }` to all connected clients |

## Security notes

- The API (8080), PostgreSQL (5432), and RabbitMQ (5672, management UI 15672) are bound to `127.0.0.1` only. The ngrok agent API (4040) is not published to the host at all — it is private to the Compose network.
- The web container (4200) is also bound to `127.0.0.1`, because nginx proxies the unauthenticated `/api/` and `/hubs/` routes to the API.
- ngrok's Traffic Policy publicly exposes only `POST /webhooks/telegram`; every other path returns a `403` from ngrok.
- There is no authentication: anyone who can reach the loopback bindings can read conversations, send replies, or receive SignalR notifications. The hub broadcasts to all clients — there is no per-conversation targeting or authorization.

## Running tests

```sh
dotnet test src/backend/ChatInbox.slnx
```

```sh
cd src/frontend/chat-inbox-web && npm test
```

The backend integration suite starts disposable PostgreSQL 17 and RabbitMQ 4.3.6 containers (Testcontainers) and disables only external Telegram/ngrok registration; no bot token or tunnel is required to run it.

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
