# Chat Inbox

Foundation for the EncodeLabs conversational-inbox challenge. This repository currently contains buildable .NET and Angular application shells, local supporting containers, and target-architecture documents—not a working Telegram inbox.

## Prerequisites

- Docker with Compose v2 for the local stack.
- A valid ngrok account authtoken for the five-service stack.
- .NET 10 SDK and Node.js 24 with npm for independent local builds (the container builds use these versions).

## Run locally

From the repository root:

```sh
cp .env.example .env
```

Replace **every** placeholder in `.env` with local values, including a valid `NGROK_AUTHTOKEN`. Keep `.env` private; it is Git-ignored. Then run:

```sh
docker compose config --quiet
docker compose up
```

In another terminal, inspect the services:

```sh
docker compose ps
curl -i http://localhost:4200/inbox
curl -i http://localhost:8080/
```

The web shell is at <http://localhost:4200/inbox> and the API listens on <http://localhost:8080>. The API has no business endpoints yet, so an HTTP 404 at `/` is expected. PostgreSQL (`5432`), RabbitMQ AMQP (`5672`), and RabbitMQ management (<http://localhost:15672>) are published on loopback only. The ngrok tunnel targets the API but does **not** register a Telegram webhook. After source changes, `docker compose up --build` can rebuild the application images.

To build the applications independently from the repository root:

```sh
dotnet restore src/backend/ChatInbox.slnx
dotnet build src/backend/ChatInbox.slnx --no-restore
npm --prefix src/frontend/chat-inbox-web ci
npm --prefix src/frontend/chat-inbox-web run build
```

## Architecture

These documents describe the **target architecture**, not completed integrations:

- [ADR 001 — PostgreSQL](docs/adr/ADR-001-database.md)
- [ADR 002 — SignalR](docs/adr/ADR-002-realtime.md)
- [ADR 003 — Telegram webhook and RabbitMQ](docs/adr/ADR-003-webhook-rabbitmq.md)
- [C4 context diagram](docs/architecture/c4-context.svg)
- [C4 container diagram](docs/architecture/c4-containers.svg)

Inbound and outbound Telegram messaging, RabbitMQ consumption, conversation persistence, SignalR updates, and the end-to-end message flow are not implemented. Full five-service startup has not been verified without a valid ngrok token; independent builds or running supporting containers do not prove the challenge works end to end.
