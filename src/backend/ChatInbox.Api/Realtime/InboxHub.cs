using Microsoft.AspNetCore.SignalR;

namespace ChatInbox.Api.Realtime;

/// <summary>
/// Server-to-client only: the operator inbox listens for "messageStored" and refetches
/// from the REST API, which stays the authoritative source of truth.
/// </summary>
public sealed class InboxHub : Hub;
