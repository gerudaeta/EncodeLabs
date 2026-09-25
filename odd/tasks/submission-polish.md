# Submission Polish

## Objective

Make the repository ready for evaluation: documentation and diagrams describe the implemented system, and an evaluator can start and exercise every flow quickly.

## Problem and why

C4 diagrams and some ADR statuses still describe the foundation PR ("target", "planned", "future") although inbound, persistence, replies and SignalR are implemented and live-verified (PR #2, PR #3).

## Scope

- C4 L1/L2 SVGs reflect the implemented architecture (titles, labels, legend, `<desc>`); no "planned/future" for implemented flows. Keep only genuinely absent items (authentication) marked as not implemented.
- ADR-001 and ADR-003 status and "current boundary" lines match the implementation (ADR-002 already updated).
- README: evaluator-oriented quick start (prerequisites, `.env` setup, `docker compose up`, how to exercise each flow, where to see results), links to ADRs, diagrams and trackers; honest limitations.
- Repository sweep for stale statements, leftovers and inconsistencies across docs; report findings, fix only documentation.

## Constraints

- Branch `chore/submission-polish`, stacked on `feature/chat-inbox-ui-replies` (PR #3).
- Documentation only; no source or configuration changes. Artifacts in English. Conventional commits, no AI attribution.
- SVGs must remain valid XML and readable.

## Tasks

- [x] **POL-01 — Diagrams:** Update both C4 SVGs.
- [x] **POL-02 — ADRs and README:** Update ADR-001/003 status lines and rewrite README for evaluators.
- [x] **POL-03 — Sweep:** Docs consistency sweep and findings report.

## Checks

- `xmllint --noout docs/architecture/*.svg` — passed for both files.
- `rg -n -i "planned|future|not implemented|foundation PR" docs README.md` reviewed; remaining hits justified.

## Progress

- 2026-09-25 Created from `feature/chat-inbox-ui-replies` at `6e6e0bd`.
- 2026-09-25 POL-01: Retitled both SVGs (`C4 L1 — System context`, `C4 L2 — Containers`), rewrote subtitles/`<desc>`/relationship labels to state the implemented, live-verified flow, removed the now-inaccurate dashed "planned/future" styling on every implemented edge (arrows are solid), and repurposed the legend (`Component` / `Implemented flow`). Authentication is called out as not implemented in both diagrams. `xmllint --noout` passed on both files.
- 2026-09-25 POL-02: ADR-001 and ADR-003 status changed to "Accepted and implemented"; their "Current boundary" bullets replaced with "Implementation" bullets describing the actual EF Core schema/idempotency and the durable queue/DLQ/registration behavior. README rewritten for an evaluator: one-paragraph overview, architecture links, prerequisites, `.env` quick start with per-variable guidance, a numbered "exercise each flow" section, an endpoint table, security notes (loopback bindings, ngrok Traffic Policy scope, no-auth caveat), test commands for both suites, condensed diagnose/recover guidance, limitations, and links to all four `odd/tasks/*.md` trackers.
- 2026-09-25 POL-03: Swept `docs/`, README, and `odd/` for stale/contradictory statements. Fixed: leftover "future" wording in ADR-002's Context/Decision prose (its Status and Consequences were already updated but the body text was not); an inaccurate README claim that the ngrok agent API is "bound to 127.0.0.1" — `docker-compose.yml` publishes no host port for it at all, so it is private to the Compose network, not loopback-bound (fixed in the rewritten README). Could not read or sweep `.env.example`: the tool permission layer denies both `Read` and `Bash` access to that path (matches on the `.env*` pattern), so its comments are unreviewed — flagged for the user to check directly. No source or config changes were made or recommended beyond documentation.

## Next step

None — all three tasks complete. Remaining follow-up: user should review `.env.example` comments directly, since this session could not read that file.
