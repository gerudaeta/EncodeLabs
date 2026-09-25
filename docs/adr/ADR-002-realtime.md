# ADR-002: SignalR for browser updates

**Status:** Accepted and implemented.

## Context

The operator's inbox should receive new-message and conversation updates without repeated manual refresh. The .NET API is the source of those updates; the Angular client needs a manageable connection lifecycle.

## Decision

Use ASP.NET Core SignalR for API-to-browser notifications. Its hub model supports targeted delivery, while the JavaScript client offers configurable reconnection; reconnection is **not enabled by default** ([SignalR overview](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-10.0); [JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0)). Persisted data remains authoritative; a notification prompts the client to reconcile state rather than replacing storage.

## Alternatives and tradeoffs

Polling is simpler but adds repeated requests and update delay. Raw WebSockets offer direct transport control but require more protocol, targeting, and reconnect code. SignalR adds a framework connection boundary instead of those custom mechanisms.

## Consequences

- **Positive:** Lower-latency browser updates with a .NET-supported client/server model.
- **Negative:** Connection lifecycle, reconnection, group membership, and eventual authorization of subscriptions must be designed and tested; notifications are not durable delivery.
- **Implementation:** Hub `/hubs/inbox` broadcasts `messageStored` with the conversation ID after a newly committed inbound message and after a persisted reply; notification failures are logged and never block persistence or acknowledgement. The Angular client uses `withAutomaticReconnect()` and refetches the list and open thread. Subscriptions are not authenticated or targeted yet.
