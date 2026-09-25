# Chat Inbox

Inbound Telegram text-message slice for the EncodeLabs conversational-inbox challenge. The .NET API validates a secret-bearing webhook, confirms durable RabbitMQ publication, consumes with manual acknowledgements, persists idempotently in PostgreSQL, and exposes local conversation/message queries. The Angular app is still a shell; outbound replies, SignalR, and a functional inbox UI are not implemented.

## Prerequisites and security boundary

- Docker with Compose v2; .NET 10 SDK for independent backend tests. The frontend container build uses Node.js 24.
- A Telegram bot token, a **distinct** random webhook secret, and an ngrok authtoken are needed only for a credentialed five-service run. Never commit or paste them into logs or tickets.
- The API's unauthenticated GET routes are intended **only for localhost**. Compose binds host port 8080 to `127.0.0.1`; its ngrok Traffic Policy is configured to deny everything except `POST /webhooks/telegram`. **This public-deny policy has not yet been proven against the selected ngrok Docker image or a live tunnel. Do not expose the stack publicly until that gate is tested.** The ngrok agent API on port 4040 is private to the Compose network.

## Configure and run

Once the selected-image public-policy gate has been validated and a credentialed run is explicitly authorized, copy `.env.example` to the Git-ignored `.env` from the repository root and replace every placeholder. Keep the PostgreSQL connection string and RabbitMQ URI consistent with their service credentials. Use a webhook secret accepted by Telegram (letters, digits, `_`, or `-`; 1–256 characters). Never use the placeholder values with a public tunnel.

```sh
cp .env.example .env
docker compose config --quiet
docker compose up --build
```

Compose starts the API, Angular shell, PostgreSQL, RabbitMQ 4.3.6, and ngrok. With valid credentials, the API is configured to discover the v3 ngrok agent endpoint at `http://ngrok:4040/api/endpoints`, select exactly one HTTPS endpoint forwarding to `http://api:8080`, and register `/webhooks/telegram` with Telegram. It waits for migration, broker publisher, consumer subscription, and HTTP-listener startup. Registration retries transient provider failures; readiness stays red until registration is reconciled. **Automatic registration is implemented but has not been verified with the selected ngrok image or real Telegram.** Do not treat container start as delivery proof.

```sh
docker compose ps
curl -i http://127.0.0.1:8080/health/ready
curl -i 'http://127.0.0.1:8080/api/conversations?limit=50'
```

`/health/ready` returns 200 only when registration and consumer readiness are true; otherwise it returns 503 with a redacted failure category and any available pending-update count. The local GET routes use `limit` (1–100) and an opaque `nextCursor` returned by the previous page. For a known conversation ID, query `http://127.0.0.1:8080/api/conversations/{id}/messages?limit=50`. The Angular shell is at `http://localhost:4200/inbox` but is not yet connected to these APIs.

For a **local-only** backend verification without bot or ngrok credentials:

```sh
dotnet test src/backend/ChatInbox.slnx
dotnet build src/backend/ChatInbox.slnx --no-restore
```

The integration suite starts disposable PostgreSQL 17 and RabbitMQ 4.3.6 containers and the production API host. It disables only external registration; it does not replace the publisher, consumer, database, migrations, or query endpoints. A successful local test does **not** prove the public ngrok route or Telegram delivery.

## Diagnose and recover

Use `docker compose logs api rabbitmq ngrok` and `/health/ready` to inspect categories; avoid logging HTTP request URLs containing the bot token or message bodies. RabbitMQ management is bound to `127.0.0.1:15672`. Inspect `chat-inbox.inbound.q` depth/unacknowledged count and `chat-inbox.dead.q` count there. The inbound quorum queue retries transient store failures with a delivery limit of five; after exhaustion, messages move to the DLQ and are **not** considered persisted. The API fails/stops visibly on terminal consumer broker cancellation, and Compose's `restart: on-failure` restarts it.

After fixing a root cause, inspect a DLQ item's `x-death` reason and update ID, check whether the update was already committed, then replay **one item at a time** to the inbound exchange with its original routing key and persistent delivery. Verify the local GET result and queue depth after each replay; do not purge or bulk-replay blindly. A broker-confirmed webhook response means accepted by RabbitMQ, not committed to PostgreSQL. If PostgreSQL is unavailable, the update may eventually dead-letter rather than appear in the inbox.

For an authorized live smoke, compare Telegram `getWebhookInfo.url` with the discovered HTTPS endpoint plus `/webhooks/telegram`, verify `pending_update_count` and delivery errors, send one text update, then confirm it appears once through the localhost GET routes. Separately verify public GET denial, public webhook allow behavior, API LAN denial, and that port 4040 is not externally reachable. **None of these credentialed or selected-image checks has been run yet.** Use an operator-controlled credential channel, not a token-bearing URL copied into shell history.

Stopping the stack does not delete Telegram's webhook or discard Telegram's backlog. Before replacing or rolling back a live deployment, coordinate whether the previous webhook URL should be restored or Telegram's webhook should be deleted. Revert application/configuration changes separately; never automatically drop PostgreSQL tables or purge RabbitMQ queues/DLQ, since both contain durable state. Schema and topology rollback require a rehearsal on a copied data set.

## Architecture

- [ADR 001 — PostgreSQL](docs/adr/ADR-001-database.md)
- [ADR 002 — SignalR](docs/adr/ADR-002-realtime.md) (future)
- [ADR 003 — Telegram webhook and RabbitMQ](docs/adr/ADR-003-webhook-rabbitmq.md)
- [C4 context diagram](docs/architecture/c4-context.svg)
- [C4 container diagram](docs/architecture/c4-containers.svg)
