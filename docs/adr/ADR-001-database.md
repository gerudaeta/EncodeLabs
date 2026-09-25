# ADR-001: PostgreSQL for conversation data

**Status:** Accepted and implemented.

## Context

Contacts, conversations, and messages need durable storage. Their relationships, message ordering, and concurrent operator access favor a transactional relational store with enforceable keys and constraints.

## Decision

Use PostgreSQL as the system of record for these records. PostgreSQL supports relational constraints and transactions ([constraints](https://www.postgresql.org/docs/current/ddl-constraints.html); [transactions](https://www.postgresql.org/docs/current/tutorial-transactions.html)).

## Alternatives and tradeoffs

SQLite would simplify a single-process local setup but is a weaker fit for a separately deployed API with concurrent writers. A document store would ease variable Telegram payload storage but move more relationship integrity and conversation-query work into application code. Neither advantage outweighs PostgreSQL's fit for the core records.

## Consequences

- **Positive:** Explicit relational integrity and a conventional query model for inbox history.
- **Negative:** Schema migrations, backups, and database operations become required work.
- **Implementation:** EF Core migrations model contacts, conversations, and messages with a unique constraint on the inbound Telegram `update_id`, giving idempotent persistence on webhook redelivery. Outbound replies reuse the same schema with a nullable `update_id`.
