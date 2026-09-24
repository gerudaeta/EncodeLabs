# Chat Inbox Project Foundation

## Goal

Create a small, buildable backend and frontend foundation for the EncodeLabs conversational-inbox challenge. This stage establishes dependency direction and development tooling without pretending that Telegram, messaging, persistence, or real-time behavior already works.

## Scope

- Target .NET 10 and Angular 22, the versions selected for this project.
- Generate a .NET solution with `ChatInbox.Domain`, `ChatInbox.Application`, `ChatInbox.Infrastructure`, and `ChatInbox.Api`.
- Generate one Angular standalone application under `src/frontend/chat-inbox-web`, with routing enabled and server-side rendering disabled.
- Add an initial `inbox` feature location, but no fabricated conversations or production UI behavior.
- Add minimal repository configuration and instructions needed to build both applications locally.
- Verify that the backend and frontend build independently.

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

## Deferred integration work

The challenge's inbound Telegram webhook, RabbitMQ consumer, PostgreSQL model, SignalR notifications, outgoing Telegram replies, automatic ngrok webhook registration, and `docker compose up` flow remain required for the finished submission. They are explicitly outside this foundation stage and must not be described as complete afterward.

## Validation and acceptance

1. The solution projects build under the installed .NET 10 SDK.
2. The Angular application builds with Angular 22 using the installed compatible Node.js runtime, without requiring a global Angular CLI installation.
3. Project references match the dependency direction above.
4. The generated applications contain no credentials or challenge functionality presented as working.
5. Existing unrelated files, including the untracked `.atl/` directory, remain untouched.

## Next stage

Implement the smallest end-to-end inbound-message slice and its supporting infrastructure, then complete the remaining flows and challenge deliverables in separately verifiable increments.
