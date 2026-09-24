# Chat Inbox Project Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver independently buildable .NET and Angular foundations, a root Compose startup path, three complete ADRs, and C4 context/container diagrams for the EncodeLabs conversational-inbox challenge, then open the first pull request against `develop`.

**Architecture:** A single ASP.NET Core Minimal API is the composition root for four projects with inward-only references. A separate standalone Angular application starts at an empty `inbox` feature location. Root Compose builds and runs both applications alongside PostgreSQL, RabbitMQ, and an ngrok tunnel. ADRs and C4 diagrams document the target flows while distinguishing those supporting containers from Telegram, messaging, persistence, and real-time integrations not yet implemented.

**Tech Stack:** .NET 10 SDK, ASP.NET Core Minimal APIs, Angular 22, compatible Node.js, npm, Docker Compose, PostgreSQL, RabbitMQ, ngrok, GitHub pull request.

**Spec:** `docs/superpowers/specs/2026-09-24-project-foundation-design.md`

## Global Constraints

- Target .NET 10 and Angular 22; use official generators and no global Angular CLI requirement.
- Backend projects: `ChatInbox.Domain`, `ChatInbox.Application`, `ChatInbox.Infrastructure`, `ChatInbox.Api`.
- Frontend: one strict, standalone, routed Angular app at `src/frontend/chat-inbox-web`, with SSR disabled.
- Root `docker-compose.yml` must declare API, frontend, PostgreSQL, RabbitMQ, and ngrok; after filling local `.env`, plain `docker compose up` is the required acceptance entry point. `--build` is optional for rebuilding after source changes.
- First PR includes three complete ADRs, each at most one page using the supplied template, and both C4 diagrams as SVG; these describe the target, not implemented end-to-end behavior.
- Do not add MediatR, AutoMapper, repository abstractions, empty service interfaces, NgRx, a component library, authentication, or mock message data.
- Do not claim Telegram webhook registration, RabbitMQ consumption, PostgreSQL persistence, SignalR, outgoing replies, or functional message flow works in this increment. Compose startup is a separate, narrower claim requiring its own observed check.
- Preserve unrelated files, especially untracked `.atl/`; do not stage it.
- Use Conventional Commits without AI attribution. The requested PR targets `develop`; do not merge it.

## File Structure

| Path | Responsibility |
| --- | --- |
| `src/backend/ChatInbox.slnx` | Solution membership for the four backend projects. |
| `src/backend/ChatInbox.Domain/` | Dependency-free domain project; no invented entities yet. |
| `src/backend/ChatInbox.Application/` | Application project referencing Domain; no speculative ports. |
| `src/backend/ChatInbox.Infrastructure/` | Infrastructure project referencing Application; no adapters yet. |
| `src/backend/ChatInbox.Api/` | Only executable host and Minimal API composition root. |
| `src/frontend/chat-inbox-web/` | Generated Angular application and local npm lockfile. |
| `src/frontend/chat-inbox-web/src/app/inbox/` | Initial feature location with a minimal route/component and no fabricated data. |
| `src/backend/ChatInbox.Api/Dockerfile` | Build and run the API from the .NET solution with the repository root as Docker build context. |
| `src/frontend/chat-inbox-web/Dockerfile` | Build Angular with npm and serve the compiled output from a web server. |
| `docker-compose.yml` | Root orchestration for API, frontend, PostgreSQL, RabbitMQ, and ngrok. |
| `.env.example` | Safe template for local PostgreSQL/RabbitMQ credentials and `NGROK_AUTHTOKEN`; no real secrets. |
| `docs/adr/ADR-001-database.md` | One-page PostgreSQL decision, alternatives, and consequences. |
| `docs/adr/ADR-002-realtime.md` | One-page SignalR decision, alternatives, and consequences. |
| `docs/adr/ADR-003-webhook-rabbitmq.md` | One-page webhook/RabbitMQ ingestion decision, alternatives, and consequences. |
| `docs/architecture/c4-context.svg` | C4 level 1 target-system context, with implementation-status key. |
| `docs/architecture/c4-containers.svg` | C4 level 2 target containers and directional flows, with implementation-status key. |
| `.gitignore` | Ignore .NET and Angular generated build output and local secrets without hiding source or lockfiles. |
| `README.md` | Exact local build/Compose startup commands and explicit foundation-stage limits. |

## Review Focus

1. Installed SDK is not .NET 10: verify `dotnet --list-sdks` before generation and `dotnet build` after it; report a missing runtime rather than silently changing the target.
2. Accidental outward project reference: inspect every `.csproj` reference and prove `Domain` has none, `Application → Domain`, `Infrastructure → Application`, and `Api → Application + Infrastructure`.
3. Angular CLI resolves to a different major or incompatible Node: check `node --version` and the generated `@angular/core`/`@angular/cli` major in `package.json` before accepting the build.
4. Generator sample endpoint, weather data, or inbox mock data survives: inspect generated source and use source search to confirm that none is presented as challenge behavior.
5. Compose has unresolved variables, wrong build contexts, or premature readiness assumptions: validate the rendered configuration with filled local values, build images, inspect `docker compose ps`/logs, and do not equate running PostgreSQL/RabbitMQ with API integrations.
6. ADRs or diagrams look like completion claims: verify each decision has alternatives/consequences and each diagram visibly distinguishes the target from first-PR code; parse SVG XML and check the one-page ADR limit.
7. Generated secrets/build output or pre-existing `.atl/` enter the PR: inspect `git status --short`, `git diff --cached --name-only`, and the PR diff before push/open.

---

### Task 1: Buildable backend boundary

**Files:** Create `src/backend/ChatInbox.slnx` and the four project directories in the file map; modify generated `src/backend/ChatInbox.Api/Program.cs` only to remove sample behavior and leave a minimal truthful host.

**Interfaces:** Produces the project-reference graph in the spec. There are no application interfaces or business methods yet.

- [ ] **Step 1: Confirm the toolchain and generate the solution.** Run from repository root:

  ```bash
  dotnet --list-sdks
  dotnet new sln -n ChatInbox -o src/backend
  dotnet new classlib -n ChatInbox.Domain -f net10.0 -o src/backend/ChatInbox.Domain
  dotnet new classlib -n ChatInbox.Application -f net10.0 -o src/backend/ChatInbox.Application
  dotnet new classlib -n ChatInbox.Infrastructure -f net10.0 -o src/backend/ChatInbox.Infrastructure
  dotnet new webapi -n ChatInbox.Api -f net10.0 -o src/backend/ChatInbox.Api --no-https
  dotnet sln src/backend/ChatInbox.slnx add src/backend/ChatInbox.Domain/ChatInbox.Domain.csproj src/backend/ChatInbox.Application/ChatInbox.Application.csproj src/backend/ChatInbox.Infrastructure/ChatInbox.Infrastructure.csproj src/backend/ChatInbox.Api/ChatInbox.Api.csproj
  ```

  Expected: the SDK list contains `10.x`; all four projects appear in `dotnet sln src/backend/ChatInbox.slnx list`. If the SDK is absent, stop this task and report it.

- [ ] **Step 2: Set the exact dependency direction.** Run:

  ```bash
  dotnet add src/backend/ChatInbox.Application/ChatInbox.Application.csproj reference src/backend/ChatInbox.Domain/ChatInbox.Domain.csproj
  dotnet add src/backend/ChatInbox.Infrastructure/ChatInbox.Infrastructure.csproj reference src/backend/ChatInbox.Application/ChatInbox.Application.csproj
  dotnet add src/backend/ChatInbox.Api/ChatInbox.Api.csproj reference src/backend/ChatInbox.Application/ChatInbox.Application.csproj src/backend/ChatInbox.Infrastructure/ChatInbox.Infrastructure.csproj
  ```

  Remove each generated `Class1.cs`, the template's `WeatherForecast` endpoint and sample HTTP request, and any template OpenAPI wiring/package that exists only for the sample. Keep `Program.cs` to the honest minimum: `CreateBuilder`, `Build`, `Run`; add no placeholder integration or health claim. Inspect actual generated files before deleting; template output varies by installed SDK.

- [ ] **Step 3: Verify the backend boundary and build.** Run:

  ```bash
  dotnet sln src/backend/ChatInbox.slnx list
  dotnet list src/backend/ChatInbox.Application/ChatInbox.Application.csproj reference
  dotnet list src/backend/ChatInbox.Infrastructure/ChatInbox.Infrastructure.csproj reference
  dotnet list src/backend/ChatInbox.Api/ChatInbox.Api.csproj reference
  dotnet build src/backend/ChatInbox.slnx --no-restore
  ```

  If no restore assets exist, run `dotnet restore src/backend/ChatInbox.slnx` first, then repeat the build. Inspect Domain's `.csproj` to confirm no project or framework/package dependency was introduced. Search backend source for `WeatherForecast` and remove any remaining sample references. Record exact command outcomes.

- [ ] **Step 4: Commit the backend work unit.** Stage only `src/backend/` source and solution files, excluding `bin/` and `obj/`; inspect the staged names and commit `feat(backend): scaffold clean architecture foundation` only after the checks pass.

### Task 2: Buildable frontend foundation

**Files:** Create `src/frontend/chat-inbox-web/` and `src/frontend/chat-inbox-web/src/app/inbox/`; modify only generated Angular app routes/root component as needed to point to a minimal inbox route without sample content.

**Interfaces:** Produces a route at `/inbox` and a minimal standalone inbox component. No API contract or mocked conversations exist.

- [ ] **Step 1: Confirm Node and generate with a pinned, local CLI.** Run from repository root:

  ```bash
  node --version
  npm --version
  npx --yes --package=@angular/cli@22 ng new chat-inbox-web --directory=src/frontend/chat-inbox-web --routing --style=css --ssr=false --standalone --strict --skip-git --package-manager=npm
  ```

  Expected: the generated `package.json` pins Angular framework and CLI to major `22`; its lockfile is present. If Angular CLI rejects an option or Node is incompatible, inspect the CLI's `ng new --help`/error and adjust only the unsupported generator flag; do not change Angular major or install a global CLI.

- [ ] **Step 2: Keep one truthful feature entry point.** Create `src/app/inbox/inbox.component.ts` as a standalone component whose template identifies the empty inbox foundation without conversations, send controls, or fake status. Set the default route to redirect to `/inbox` and lazy-load that component with `loadComponent`; leave the root template as a router outlet. Do not add state management, API clients, or SignalR clients. Use the generated Angular 22 file conventions and inspect generated route/root files before editing.

- [ ] **Step 3: Verify versions, routing source, and production build.** Run:

  ```bash
  node -p "require('./src/frontend/chat-inbox-web/package.json').dependencies['@angular/core']"
  node -p "require('./src/frontend/chat-inbox-web/package.json').devDependencies['@angular/cli']"
  npm --prefix src/frontend/chat-inbox-web run build
  ```

  Expected: both package entries are `22.x` (possibly semver-ranged) and the build exits 0. Inspect the route and inbox component source for mock data or misleading production behavior. Record exact command outcomes.

- [ ] **Step 4: Commit the frontend work unit.** Stage only `src/frontend/chat-inbox-web/` source/config/lockfile, excluding `node_modules/` and build output; inspect staged names and commit `feat(frontend): scaffold standalone inbox application` after successful build.

### Task 3: Root Compose startup

**Files:** Create `docker-compose.yml`, `.env.example`, `src/backend/ChatInbox.Api/Dockerfile`, and `src/frontend/chat-inbox-web/Dockerfile`; modify `.gitignore` to exclude `.env`, `.NET bin/`/`obj/`, and Angular `node_modules/`/`dist/`/`.angular/` without excluding the lockfile or source.

**Interfaces:** Produces five Compose services: `api` (container port 8080), `web` (container port 80), `postgres` (5432), `rabbitmq` (5672 and 15672 management), and `ngrok` (tunnel target `api:8080`). No API database connection, queue consumer, or Telegram registration is implied.

- [ ] **Step 1: Create the two production-oriented Dockerfiles.** The API image uses the repository root as its build context so it can copy `src/backend/` as a whole, restores/publishes `ChatInbox.Api.csproj` in a .NET 10 SDK stage, and runs the published assembly in an ASP.NET Core 10 runtime image with `ASPNETCORE_URLS=http://+:8080`. The frontend image runs `npm ci` and `npm run build` in a compatible Node stage, then copies Angular's actual browser output directory (inspect `angular.json`/build output first) into a static web-server image. Do not add frontend API proxy rules or backend ports that do not exist yet.

- [ ] **Step 2: Create Compose and safe local configuration.** Declare the exact five services in root `docker-compose.yml`. Build `api` and `web` from their Dockerfiles; publish host ports 8080 and 4200 respectively. Use PostgreSQL and RabbitMQ management images with named data volumes, local development credentials read from `.env`, and native health checks (`pg_isready` and `rabbitmq-diagnostics -q ping`). Start ngrok with `NGROK_AUTHTOKEN` from `.env` and a tunnel to `api:8080`; do not call Telegram's `setWebhook` or treat the tunnel URL as automatically registered. `.env.example` contains obvious replace-before-run values only; Compose requires nonempty token/password values via interpolation checks. Keep the API independent of database/queue health until it actually consumes those services.

- [ ] **Step 3: Check rendered configuration, images, and startup.** After copying `.env.example` to an ignored local `.env` and filling valid values, run:

  ```bash
  docker compose config --quiet
  docker compose up
  # In a second terminal while the stack is running:
  docker compose ps
  docker compose logs --tail=100 api web postgres rabbitmq ngrok
  # Stop the foreground stack, then run:
  docker compose down
  ```

  Expected: configuration resolves, both images build, and plain `docker compose up` starts all five containers without restart loops. Validate the frontend on `http://localhost:4200` and API process on port 8080 without inventing an endpoint or health response. A valid ngrok token and Docker availability are prerequisites; if either is missing, run the checks possible without them and record startup as unverified. Never print `.env` or an unredacted rendered configuration in logs/review output.

- [ ] **Step 4: Commit the Compose work unit.** Stage only the two Dockerfiles, `docker-compose.yml`, `.env.example`, and `.gitignore`; inspect staged paths for `.env` and generated output, then commit `feat(dev): add root compose startup` only after reporting the observed checks.

### Task 4: Complete three architecture decisions

**Files:** Create `docs/adr/ADR-001-database.md`, `docs/adr/ADR-002-realtime.md`, and `docs/adr/ADR-003-webhook-rabbitmq.md`.

**Interfaces:** Produces the three required, one-page decisions consumed by the C4 diagrams and linked from the README. They describe the target architecture; they do not imply implemented adapters or data flows.

- [ ] **Step 1: Use the supplied ADR template consistently.** For each document, retain the template's headings/order and fill every required field. Cover context, decision, at least one credible alternative, rationale/tradeoffs, and positive/negative consequences. Keep each ADR within one rendered page; do not leave placeholder sections or call the decision implemented.

- [ ] **Step 2: Write `ADR-001-database.md`.** Decide PostgreSQL as the future durable store for contacts, conversations, and messages. Compare a realistic alternative (for example SQLite for simpler local setup or a document store for flexible payloads); explain why relational constraints, conversation/message queries, and concurrent access favor PostgreSQL here. Acknowledge schema/migration and operational costs. State that this PR only starts the PostgreSQL container and does not contain a persistence model or EF integration.

- [ ] **Step 3: Write `ADR-002-realtime.md`.** Decide SignalR for future API-to-browser conversation updates. Compare polling or raw WebSockets with explicit latency, reconnection, and complexity tradeoffs. Record consequences such as connection lifecycle/group targeting and an eventual authorization boundary; do not add auth or a hub in this foundation. State that this PR only starts the Angular and API containers.

- [ ] **Step 4: Write `ADR-003-webhook-rabbitmq.md`.** Decide short-lived inbound Telegram webhook handling that publishes to RabbitMQ, with a background consumer in the single .NET host performing durable processing. Compare synchronous webhook-to-database handling and/or a separate Worker process; address retries, duplicate delivery/idempotency, acknowledgement ordering, and failure observability without promising exactly-once processing. State that Compose starts RabbitMQ/ngrok but does not register the Telegram webhook or implement a publisher/consumer.

- [ ] **Step 5: Verify and commit the decisions.** Inspect each rendered Markdown document against the supplied template and one-page constraint, and search for placeholder wording or claims that Telegram/RabbitMQ/PostgreSQL/SignalR integration works. Stage only the three ADR files; inspect staged paths and commit `docs(architecture): record foundation decisions` after checks pass.

### Task 5: Deliver C4 context and container diagrams

**Files:** Create `docs/architecture/c4-context.svg` and `docs/architecture/c4-containers.svg`.

**Interfaces:** Produces two self-contained, readable SVGs linked from the README. Both are explicitly labeled `Target architecture`; a legend distinguishes `Foundation PR: container or app shell present` from `Future integration: not implemented`.

- [ ] **Step 1: Resolve diagram style before drawing.** Follow the `diagram-design` skill's project profile/style-guide gate rather than silently using default branding. If that gate requires a user style choice, stop diagram creation and relay the question; do not treat a default theme as implicitly approved. Use a restrained C4 architecture layout with accessible text and consistent status styling.

- [ ] **Step 2: Create C4 level 1 context SVG.** Show one operator using Chat Inbox, Telegram as the external messaging system, and the intended inbound/outbound exchanges. Draw Chat Inbox as one system boundary; do not expose PostgreSQL, RabbitMQ, or internal API details at level 1. Title the diagram `C4 L1 — Target architecture` and visibly note that Telegram message exchange is planned, not working in the first PR.

- [ ] **Step 3: Create C4 level 2 container SVG.** Within Chat Inbox, show Angular web app, the single .NET API host (future HTTP webhook and background RabbitMQ consumer within that host), RabbitMQ, and PostgreSQL; show ngrok as the external tunnel to the API and Telegram/Bot API as external. Label directional target flows: operator → Angular → API; Telegram → ngrok → API webhook → RabbitMQ → API consumer → PostgreSQL → API/SignalR → Angular, plus API → Telegram Bot API for replies. Do not depict a separate deployed Worker or show a completed connection merely because its supporting container starts in Compose. Keep the status key and `Target architecture` title visible.

- [ ] **Step 4: Validate semantics, XML, and presentation.** Parse both SVGs as XML:

  ```bash
  python3 -c 'import xml.etree.ElementTree as ET; [ET.parse(path) for path in ("docs/architecture/c4-context.svg", "docs/architecture/c4-containers.svg")]; print("SVG XML valid")'
  ```

  Expected: `SVG XML valid`. Open/view both diagrams to check text legibility, arrow direction, no clipped labels, and clear implemented-versus-future markings. Cross-check their nodes and flows against all three ADRs and the actual first-PR Compose/app shells.

- [ ] **Step 5: Commit the diagrams.** Stage only the two SVGs; inspect staged paths and commit `docs(architecture): add C4 target diagrams` after checks pass.

### Task 6: Repository handoff and first pull request

**Files:** Modify `README.md`; do not modify `.atl/`.

**Interfaces:** Produces reproducible build/start instructions, links to the three ADRs and two C4 diagrams, and a PR against `develop`. This does not supply challenge integrations.

- [ ] **Step 1: Document the actual path and limits.** In `README.md`, name .NET 10, Angular 22/compatible Node, Docker Compose, and an ngrok account token as prerequisites. Show independent build commands plus `cp .env.example .env`, local value replacement, `docker compose config --quiet`, plain `docker compose up`, and `docker compose ps` in another terminal. Mention `docker compose up --build` only as an optional rebuild command after source changes. Link all three ADRs and both C4 SVGs by their exact repository paths, and explain that they depict target decisions rather than completed Telegram flow. Explain exposed ports and that the ngrok tunnel is not Telegram webhook registration. State that the PR is foundation-only: inbound/outbound Telegram, queue consumption, persisted conversations, SignalR, and end-to-end message flow are deferred.

- [ ] **Step 2: Re-run builds and review the final diff.** Run:

  ```bash
  dotnet restore src/backend/ChatInbox.slnx
  dotnet build src/backend/ChatInbox.slnx --no-restore
  npm --prefix src/frontend/chat-inbox-web ci
  npm --prefix src/frontend/chat-inbox-web run build
  docker compose config --quiet
  python3 -c 'import xml.etree.ElementTree as ET; [ET.parse(path) for path in ("docs/architecture/c4-context.svg", "docs/architecture/c4-containers.svg")]; print("SVG XML valid")'
  git status --short
  git diff --check
  ```

  Expected: both builds, Compose configuration, and SVG XML parsing pass; the README's five architecture links resolve; all three ADRs meet template and page limit; no whitespace errors, secrets, or generated output are tracked; and `.atl/` remains untouched. Record any Docker, token, or network limitation separately; never substitute a successful static build for observed five-service startup.

- [ ] **Step 3: Commit the handoff and open the PR.** Stage only `README.md`; commit `docs: document foundation build and architecture`. Confirm the branch is a `feature/` branch created from the actual `develop` target, and compare it with `origin/develop` before pushing. Push only that feature branch, then create a GitHub PR with base `develop`; title it for the project foundation, describe observed builds/Compose checks, include all three ADRs and both C4 diagrams, and list deferred integrations without a challenge-completion claim. Do not merge or deploy. Inspect the PR base and diff after creation and attach its URL to the Codex task.

## Self-Review

- Spec coverage: project versions, four references, Angular route/location, independent builds, root five-service Compose startup, safe environment template, all three one-page ADRs, both C4 SVGs, no fabricated functionality, `.atl/` preservation, and the requested PR are assigned above.
- Commands are exact where generator behavior is stable; generated file editing deliberately follows the actual Angular/.NET template output rather than assuming stale template filenames.
- Review focus checks are present in the owning tasks. A successful compile or five-service startup is foundation proof only, not webhook-to-inbox proof.
