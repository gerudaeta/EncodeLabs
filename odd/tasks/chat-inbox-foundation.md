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

- [x] **FND-01 — Backend foundation:** Created four .NET 10 projects and solution, established the reference graph, removed template samples, and observed a successful solution build.
- [x] **FND-02 — Frontend foundation:** Created Angular 22 standalone app and minimal inbox route without fake data, and observed a successful production build.
- [x] **FND-03 — Compose startup:** Created root Compose, Dockerfiles, `.env.example`, and ignore rules; validated configuration, image builds, and partial service startup. Full five-service startup remains unverified without a valid ngrok token.
- [x] **FND-04 — ADRs:** Delivered the three one-page PostgreSQL, SignalR, and webhook/RabbitMQ decisions, with alternatives and consequences.
- [x] **FND-05 — C4 diagrams:** Delivered C4 context and container SVGs, validated XML and labels/flows, and marked target versus implemented status.
- [ ] **FND-06 — Handoff and PR:** Update README with actual run instructions, re-run applicable checks, inspect staged/branch diff, push only the feature branch, and open a PR against `develop`.

## Acceptance criteria

1. Backend and frontend build independently with the selected versions.
2. `docker compose up` starts the declared stack with a valid local `.env`, or any unavailable external prerequisite is reported explicitly.
3. All three ADRs and two readable C4 SVGs exist and are linked from README.
4. PR targets `develop` and contains no secrets, generated build output, or unrelated `.atl/` files.
5. All deferred integrations are identified as pending, not described as functional.

## Progress and verification

- 2026-09-24: Approved foundation design and plan updated for Compose and architecture deliverables.
- 2026-09-24 FND-01: Commit `771b185` created the four backend projects. `dotnet build src/backend/ChatInbox.slnx --no-restore -m:1 -nr:false -v minimal` independently passed with 0 warnings and 0 errors; task review passed. Ordinary NuGet network restore was unavailable, while an offline package-free restore was reported by the implementer. Generated `bin/` and `obj/` are untracked and must be ignored by FND-03.
- 2026-09-24 FND-02: Commit `adecf32` created Angular 22 standalone app and `/inbox` route. The parent independently observed `npm --prefix src/frontend/chat-inbox-web run build` pass with approved sandbox escalation; the default sandbox exited 134 without diagnostics. The implementer reported 2 passing tests; task review passed. Minor deferred: generated tests do not assert redirect/lazy route behavior.
- 2026-09-24 FND-03: Commits `b5eeb42` and `495dd9b` added root Compose, images, local configuration, and loopback-only PostgreSQL/RabbitMQ ports after an independent security review. The parent independently observed `docker compose --env-file .env.example config --quiet` pass, verified rendered loopback bindings, and found no diff whitespace errors. The implementer reported API/web image builds and startup plus healthy PostgreSQL/RabbitMQ in separate runs. A valid ngrok token was unavailable, so full five-service startup and tunnel behavior remain unverified.
- 2026-09-24 FND-04: Commit `54bb55f` added three ADRs with the approved decisions, alternatives, consequences, and explicit implementation boundaries. The parent independently read all three documents, verified their required headings, and found no diff whitespace errors. Official reference links were checked by the implementer; runtime behavior was not tested or claimed.
- 2026-09-24 FND-05: Commit `6dc6dfc` added C4 context/container SVGs using a neutral style. The parent independently parsed both SVGs as XML and checked the required target/planned labels and diff whitespace. The implementer reported rendering and visually inspecting both full diagrams without clipping or overlap. These are static design artifacts, not runtime proof.
- 2026-09-24 FND-06 local: Commit `2cb043d` updated README with startup/build guidance, documentation links, and explicit implementation limits. Independent final verification observed .NET build pass (0 warnings/errors), Angular tests pass (2/2), Compose configuration pass, and `git diff --check develop..HEAD` pass. Angular production build exited 134 without diagnostics in the restricted sandbox but passed when the parent reran it with approved escalation. Tracked diff has no `.env`, generated build output, or `.atl/`; branch is based on local `develop` at `d586bb2`. No remote PR has been opened.
- Checks pending: full Compose startup with ngrok credential, authenticated remote branch/target verification, push, and PR creation.

## Next step

Ask for explicit approval of the credential/session to use for GitHub remote operations, then verify remote `develop`, push the feature branch, and open the PR. Do not touch unrelated `.atl/`.
