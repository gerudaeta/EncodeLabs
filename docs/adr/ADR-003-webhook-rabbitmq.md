# ADR-003: Queue inbound Telegram updates

**Status:** Accepted and implemented.

## Context

Telegram sends updates to a webhook. Database processing should not hold that HTTP request open or couple Telegram's retry path to database latency. The system has one executable .NET API host, not a separate Worker deployment.

## Decision

Keep the webhook handler short: validate the request, publish the update to RabbitMQ, and return success only after the broker confirms acceptance. A background consumer in the **same API host** processes and persists it, then acknowledges the delivery after successful processing. RabbitMQ distinguishes publisher confirms from consumer acknowledgements; neither provides end-to-end exactly-once processing ([RabbitMQ acknowledgements and confirms](https://www.rabbitmq.com/docs/confirms)). ASP.NET Core supports background tasks within a host ([hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)).

## Alternatives and tradeoffs

Synchronous webhook-to-database handling removes the broker but exposes webhook response time to database failures. A separate Worker process isolates consumption but adds deployment and coordination now. The single-host consumer is simpler initially, at the cost of shared API resources and lifecycle.

## Consequences

- **Positive:** HTTP intake and processing can fail/retry independently after confirmed enqueue.
- **Negative:** Duplicates remain possible; idempotent persistence keyed by Telegram update identity absorbs webhook redelivery and consumer retry. Publish failures, backlog, and consumer failures require monitoring.
- **Implementation:** A durable quorum queue and dead-letter queue back the inbound flow, with manual consumer acknowledgement and a bounded retry limit of five before dead-lettering. The webhook is registered automatically against the discovered ngrok HTTPS endpoint; `GET /health/ready` reports registration and consumer readiness.
