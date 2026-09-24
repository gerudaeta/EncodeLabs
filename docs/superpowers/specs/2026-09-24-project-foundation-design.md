# Chat Inbox Project Foundation

## Goal

Create a small, buildable backend and frontend foundation for the EncodeLabs conversational-inbox challenge. The root `docker-compose.yml` must start the application and its supporting services after local environment values are supplied. The first PR also delivers the challenge's three complete ADRs and C4 context/container diagrams. These documents describe the target architecture without pretending that Telegram, messaging, persistence, or real-time behavior already works.

## Scope

- Target .NET 10 and Angular 22, the versions selected for this project.
- Generate a .NET solution with `ChatInbox.Domain`, `ChatInbox.Application`, `ChatInbox.Infrastructure`, and `ChatInbox.Api`.
- Generate one Angular standalone application under `src/frontend/chat-inbox-web`, with routing enabled and server-side rendering disabled.
- Add an initial `inbox` feature location, but no fabricated conversations or production UI behavior.
- Add a root `docker-compose.yml` containing the API, Angular frontend, PostgreSQL, RabbitMQ, and ngrok, with their necessary Dockerfiles and a safe `.env.example`.
- Complete `docs/adr/ADR-001-database.md`, `ADR-002-realtime.md`, and `ADR-003-webhook-rabbitmq.md` using the supplied ADR template; each is at most one page and records context, decision, alternatives, and consequences.
- Deliver C4 level 1 and level 2 as `docs/architecture/c4-context.svg` and `docs/architecture/c4-containers.svg`, clearly titled as the target architecture and visibly distinguishing first-PR running containers from integrations still to implement.
- Add minimal repository configuration and instructions needed to build independently and start the stack with `docker compose up` after supplying a local `.env`.
- Verify that the backend and frontend build independently; verify Compose configuration and startup when Docker and an ngrok token are available.

## Backend boundaries

```text
ChatInbox.Api ──────────────> ChatInbox.Application
      │                              │
      └──> ChatInbox.Infrastructure  └──> ChatInbox.Domain
                        │
                        └───────────> ChatInbox.Application
```

`Domain` has no project references or framework dependencies. `Application` depends only on `Domain`. `Infrastructure` implements application ports when an actual infrastructure use case is introduced. `Api` is the sole executable host and composition root. Use a small ASP.NET Core Web API with Minimal APIs; keep endpoint handlers thin. Do not add MediatR, AutoMapper, repository abstractions, or empty service interfaces merely to fill layers.

## Frontend boundaries

Use Angular's official generator rather than an opinionated starter. Keep the application standalone and strict, with a feature-first `inbox` area. Do not add NgRx, a component library, authentication, SSR, or mock message data during foundation work. Actual inbox screens and API/SignalR clients belong to later functional stages.

## Containerized startup boundary

`docker-compose.yml` lives at the repository root and is the intended local entry point. Build the API from the .NET solution and serve the built Angular application from a web container; configure PostgreSQL and RabbitMQ as local infrastructure, with health checks and persistent data volumes where appropriate. Start ngrok as a tunnel to the API using a locally supplied `NGROK_AUTHTOKEN`. Keep credentials out of Git: `.env.example` documents required non-secret values/placeholders, while `.env` is ignored. Document `cp .env.example .env`, value replacement, `docker compose config`, plain `docker compose up` as the acceptance command, and how to inspect service status. `docker compose up --build` is optional when rebuilding after source changes, not a prerequisite for first startup. The API need not connect to PostgreSQL or RabbitMQ yet: running containers are not proof of implemented persistence or messaging.

## Deferred integration work

The challenge's inbound Telegram webhook, RabbitMQ consumer, PostgreSQL model, SignalR notifications, outgoing Telegram replies, and automatic ngrok webhook registration remain required for the finished submission. This foundation supplies only the Compose startup path and supporting containers; it does not claim a registered Telegram webhook or functional message flow.

## Architecture deliverables

The three ADRs make explicit, defensible choices: PostgreSQL for durable conversation data, SignalR for browser updates, and webhook → RabbitMQ → background consumer for inbound Telegram messages. Each ADR follows the supplied one-page template and states at least one credible alternative plus its tradeoff and the decision's consequences; a decision in an ADR is not a claim that its implementation exists in this PR. The C4 context diagram shows the operator, Chat Inbox, Telegram, and the intended exchanges. The C4 container diagram shows Angular, the single .NET API host (including the future consumer), PostgreSQL, RabbitMQ, and ngrok, with directional, labeled target flows. Both diagrams include a visible target-state label and a key or annotations separating what this PR starts/builds from deferred application integrations. The README links to all five deliverables and states that documentation of the target is not end-to-end proof.

## Validation and acceptance

1. The solution projects build under the installed .NET 10 SDK.
2. The Angular application builds with Angular 22 using the installed compatible Node.js runtime, without requiring a global Angular CLI installation.
3. Project references match the dependency direction above.
4. The generated applications contain no credentials or challenge functionality presented as working.
5. The root Compose file resolves with a filled local `.env`, and plain `docker compose up` builds missing API/frontend images and starts the five declared services when Docker and a valid ngrok token are available; if those prerequisites are absent, report startup as unverified rather than claiming success.
6. All three ADRs are complete, follow the supplied template, fit one page each, and distinguish architectural decisions from implemented code.
7. Both C4 SVGs parse as XML, have readable labels and directional relationships, and visibly mark target versus first-PR implementation status.
8. Existing unrelated files, including the untracked `.atl/` directory, remain untouched.

## Next stage

Implement the smallest end-to-end inbound-message slice and its supporting infrastructure, then complete the remaining flows and challenge deliverables in separately verifiable increments.
