# ADR-003: Queue inbound Telegram updates

**Status:** Accepted for the target architecture; integration not implemented in this foundation PR.

## Context

Telegram sends updates to a webhook. Database processing should not hold that HTTP request open or couple Telegram's retry path to database latency. The foundation has one executable .NET API host, not a separate Worker deployment.

## Decision

Keep the future webhook handler short: validate the request, publish the update to RabbitMQ, and return success only after the broker confirms acceptance. A background consumer in the **same API host** will process and persist it, then acknowledge the delivery after successful processing. RabbitMQ distinguishes publisher confirms from consumer acknowledgements; neither provides end-to-end exactly-once processing ([RabbitMQ acknowledgements and confirms](https://www.rabbitmq.com/docs/confirms)). ASP.NET Core supports background tasks within a host ([hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)).

## Alternatives and tradeoffs

Synchronous webhook-to-database handling removes the broker but exposes webhook response time to database failures. A separate Worker process isolates consumption but adds deployment and coordination now. The single-host consumer is simpler initially, at the cost of shared API resources and lifecycle.

## Consequences

- **Positive:** HTTP intake and processing can fail/retry independently after confirmed enqueue.
- **Negative:** Plan durable queue settings, bounded retries, poison-message visibility, and idempotent processing keyed by Telegram update identity; duplicates remain possible. Monitor publish failures, backlog, and consumer failures. Acknowledge only after durable processing, then handle uncertain outcomes safely.
- **Current boundary:** Compose starts RabbitMQ and ngrok only. This PR does not register a Telegram webhook or implement a publisher, consumer, database write, or end-to-end message flow.
