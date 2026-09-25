# Working with AI

## Tools

- **Claude Code** (desktop app, Claude Opus as orchestrator, Sonnet sub-agents): main development environment for most of the project.
- **OpenAI Codex**: used for part of the Telegram inbound slice; the work was handed over to Claude Code through the task files and a shared memory.
- **Engram** (persistent memory MCP): decisions, bug fixes and task progress survive between sessions and tools.
- **Workflow skills**: *Superpowers* (brainstorming → spec → plan, see `docs/superpowers/`) and an organic task flow where every multi-step feature gets a tracked checklist in `odd/tasks/*.md` with verification evidence.

## Agents and roles

| Agent | Role |
|---|---|
| Orchestrator (main session) | Talks to me, explores, decides the route, owns product questions, never writes large changes itself. Runs spot checks and the live Docker verification. |
| Explorer / mapper | Read-only mapping of the codebase when a decision needs more than a few files. |
| Writer (one at a time) | Implements one task list with TDD (observed RED → GREEN → refactor), runs the required test commands and reports the results. |
| Independent reviewer | Separate context that reviews a finished task against the spec and plan, without access to the writer's reasoning. |

## Representative prompts

1. *"utilizar claude design para hacer un lindo diseño, crear un branch a partir de develop"* — the agent noticed that the local `develop` was stale and branched from the remote one, preserved every `data-testid` so existing tests kept passing, and verified the result in a browser against real data.
2. *"Mensajes no leídos (requisitos 2 y 4) … hacer esta feature por favor"* — after asking for a gap audit against this brief, the missing requirement became a task document (domain → API → UI → verify) delegated to a writer with TDD and exact verification commands.
3. *"ver de implementar cache con redis, puede ser?"* — instead of complying, the agent argued against Redis (no measured bottleneck, stale unread counts, extra failure mode, C4 drift) and offered an in-memory `HybridCache` alternative, which I chose (ADR-005).

## How the output was reviewed and validated

- **Tests first.** Every feature was driven by failing tests; reports had to include the command and its observed result (backend: 118 tests incl. Testcontainers PostgreSQL/RabbitMQ; frontend: 19 Vitest tests).
- **Independent review caught real bugs** that the writer's own tests missed: a malformed Unicode cursor returning HTTP 500 instead of 400, the ngrok webhook registration using the legacy `/api/tunnels` contract instead of v3 `/api/endpoints`, and a race where the Telegram webhook could be registered before the API or the RabbitMQ consumer was ready.
- **Empirical checks over documentation.** For the cache, tag-based `HybridCache` invalidation looked right but a test proved it could miss same-tick entries; exact-key removal was used instead.
- **Live end-to-end checks** in Docker: with the real bot, messages received in real time and replies delivered to Telegram; unread count and mark-as-read exercised against the running API.
- **I kept the decisions.** Scope, pushes, PRs and public actions required my explicit approval.
