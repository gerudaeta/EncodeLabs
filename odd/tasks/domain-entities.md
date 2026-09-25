# Domain entities

## Objective
Move the `Conversation` and `Message` entities and their business rules out of `ChatInbox.Infrastructure/Persistence/InboxDbContext.cs` into the (currently empty) `ChatInbox.Domain` project, so the solution actually follows Clean Architecture and the domain can be unit-tested without EF.

## Problem / why
`ChatInbox.Domain` contains only its `.csproj`. Entities and the `MarkRead` rule live in Infrastructure next to the `DbContext`, so the domain depends on persistence. The tech test weighs architecture (20 %) and code quality / Clean Architecture (20 %) and asks that tests, if present, be unit tests over the domain.

## Scope
- `ChatInbox.Domain`: `Conversation`, `Message`, message direction as a domain type, and the domain rules that already exist (mark read, outbound never unread; creation invariants the code already enforces). No EF or infrastructure references.
- Infrastructure: EF mapping via Fluent API / `IEntityTypeConfiguration` so the database schema does not change (no new migration, or an empty-diff check proving it).
- Application/Infrastructure/Api/Tests references updated; project references follow the dependency rule (Domain ← Application ← Infrastructure ← Api).
- Domain unit tests for the rules (move/extend `MessageReadStateTests`).
- README/ADR text that mentions where entities live updated if needed.

## Constraints
- Behaviour-preserving refactor: no API contract, schema, or UI change.
- Keep it minimal: no aggregates/repositories/value-object frameworks beyond what the existing rules need.

## TDD
Enabled. Runner `dotnet test src/backend/ChatInbox.slnx`. For a behaviour-preserving move, the existing suite is the safety net (must stay green); new domain rules/tests follow RED → GREEN.

## Tasks
- [x] DOM-01 Move entities and rules to Domain, EF configuration in Infrastructure, schema unchanged, tests green.
- [ ] DOM-02 Verify: full suite, Docker rebuild of api, live smoke check.

## Acceptance criteria
- `ChatInbox.Domain` has no package/project references to EF or Infrastructure.
- `dotnet ef migrations add` would produce an empty migration (schema unchanged).
- Full backend suite green.

## Progress / evidence

DOM-01 done (commit `refactor: move conversation and message entities to domain layer`).

- `Conversation`, `Message` and `MarkRead` moved to `ChatInbox.Domain` (`Conversation.cs`,
  `Message.cs`). No EF/Infrastructure references in the Domain project (`rg` found none;
  csproj has no package/project references).
- Message direction modeled as `MessageDirection` enum (`MessageDirection.cs`) with a
  `MessageDirectionCodec` mapping to the exact persisted `"inbound"`/`"outbound"` strings via
  an EF `HasConversion`. Replaced the old internal `MessageDirections` string-constants class
  and scattered `"inbound"`/`"outbound"` literals in Infrastructure and tests that compared/set
  the entity's `Direction` (API-facing `MessageDto.Direction` stays `string` — untouched
  contract — via `.ToStorageValue()` at the mapping boundary).
- EF mapping (table/column names, keys, indexes incl. filtered `ix_messages_unread`, unique
  `ux_messages_update`, converters) moved to `IEntityTypeConfiguration<T>` classes under
  `ChatInbox.Infrastructure/Persistence/Configurations/` (`ConversationConfiguration`,
  `MessageConfiguration`, `ProcessedUpdateConfiguration`); `InboxDbContext.OnModelCreating` now
  just applies them.
- Project references: Application and Infrastructure already reached Domain transitively
  (Application → Domain; Infrastructure → Application), so no new `ProjectReference` was needed.
- Domain unit tests: `MessageReadStateTests` now constructs `ChatInbox.Domain.Message` directly
  (no EF, no DbContext) and covers MarkRead on inbound, idempotency on already-read, and
  outbound-never-unread — unchanged coverage, just against the moved type.
- Schema-unchanged proof: ran `dotnet ef migrations add SchemaCheck ...` — generated `Up`/`Down`
  were empty. Model snapshot diff was namespace-only (`ChatInbox.Infrastructure.Persistence.*` →
  `ChatInbox.Domain.*` for `Conversation`/`Message` CLR type strings; `ProcessedUpdate` stayed in
  Infrastructure and its snapshot entry is unchanged); no column/index/key/constraint change.
  Migration re-added a second time (`SchemaCheck2`) against the corrected snapshot to confirm no
  further diff, then removed (files deleted; snapshot's namespace-only diff kept, since
  `ef migrations remove` reverts to the pre-refactor snapshot and would otherwise silently
  mismatch the real model — no migration files remain under `Persistence/Migrations` beyond the
  pre-existing three).
- README/ADRs: checked for text stating where entities live; none found (the one Domain-adjacent
  README line is about test levels, not file location), so no doc changes were needed.
- `dotnet build src/backend/ChatInbox.slnx`: succeeded, 0 warnings, 0 errors.
- `dotnet test src/backend/ChatInbox.slnx`: 118/118 passed (Testcontainers PostgreSQL/RabbitMQ),
  0 failed, 0 skipped.

## Next step
DOM-02 (Docker rebuild of api + live smoke check — parent-owned).
