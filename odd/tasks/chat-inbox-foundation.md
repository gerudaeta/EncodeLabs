# Chat Inbox Foundation

## Objective

Deliver the first reviewable PR against `develop`: buildable .NET 10 and Angular 22 foundations, root Docker Compose startup, three architecture decision records, and C4 level-1/level-2 SVG diagrams.

## Problem and why

The repository has only an initial README. The technical challenge requires a reproducible local stack and defensible architecture. This increment creates the foundation and architectural artifacts without presenting the Telegram conversation flow as implemented.

## Authorized scope

- Repository: `/Users/germanudaeta/Documents/EncodeLabs`.
- Branch: `feature/chat-inbox-foundation`, targeting `develop`.
- Create and verify local source, configuration, tests/checks, and documentation; commit, push this branch, and open the first PR.
- Do not merge, deploy, edit unrelated `.atl/`, or claim Telegram/RabbitMQ/SignalR/persistence integrations work.

## Constraints and decisions

- Follow `docs/superpowers/specs/2026-09-24-project-foundation-design.md` and `docs/superpowers/plans/2026-09-24-project-foundation.md`.
- Use official .NET and Angular generators; no opinionated Clean Architecture template.
- Keep a single executable .NET API host and inward-only project references.
- `docker-compose.yml` must be at the repository root; plain `docker compose up` after completing `.env` is the startup acceptance command.
- ADRs and C4 diagrams describe the target architecture and must distinguish it from this PR's implemented behavior.
- Stage only intended files. Never commit credentials or AI attribution.
- TDD mode: unresolved from existing project/session configuration; this foundation adds no business behavior. Applicable proof is independent backend/frontend builds, Compose checks/startup, document checks, and source review. Resolve TDD mode before future behavioral implementation.

## Tasks

- [ ] **FND-01 — Backend foundation:** Create four .NET 10 projects and solution, establish the reference graph, remove template samples, and observe a successful solution build.
- [ ] **FND-02 — Frontend foundation:** Create Angular 22 standalone app and minimal inbox route without fake data, and observe a successful production build.
- [ ] **FND-03 — Compose startup:** Create root Compose, Dockerfiles, `.env.example`, and ignore rules; validate configuration, image builds, and plain `docker compose up` when prerequisites exist.
- [ ] **FND-04 — ADRs:** Deliver the three required one-page PostgreSQL, SignalR, and webhook/RabbitMQ decisions, with alternatives and consequences.
- [ ] **FND-05 — C4 diagrams:** Deliver C4 context and container SVGs, validate XML, visually inspect labels/flows, and mark target versus implemented status.
- [ ] **FND-06 — Handoff and PR:** Update README with actual run instructions, re-run applicable checks, inspect staged/branch diff, push only the feature branch, and open a PR against `develop`.

## Acceptance criteria

1. Backend and frontend build independently with the selected versions.
2. `docker compose up` starts the declared stack with a valid local `.env`, or any unavailable external prerequisite is reported explicitly.
3. All three ADRs and two readable C4 SVGs exist and are linked from README.
4. PR targets `develop` and contains no secrets, generated build output, or unrelated `.atl/` files.
5. All deferred integrations are identified as pending, not described as functional.

## Progress and verification

- 2026-09-24: Approved foundation design and plan updated for Compose and architecture deliverables. No source tasks completed yet.
- Checks pending: .NET build, Angular build, Compose config/startup, ADR review, SVG parse/visual review, final diff, remote PR checks.

## Next step

Execute FND-01, then advance only after its observed checks; update this file and its Engram mirror after each task.
