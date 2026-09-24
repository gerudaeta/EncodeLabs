# Chat Inbox Inbound Telegram Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver one inspectable Telegram text-update path from authenticated webhook through confirmed RabbitMQ publication, same-host consumption, idempotent PostgreSQL persistence, and local-only read APIs.

**Architecture:** Keep `ChatInbox.Api` as the sole executable host. Minimal API endpoints call application contracts; infrastructure adapters own Telegram HTTP, RabbitMQ, and PostgreSQL. The webhook acknowledges only confirmed, routable broker acceptance; the consumer acknowledges only committed or already-committed database work. The ngrok agent supplies its URL to an API-hosted registration service.

**Tech Stack:** .NET 10 Minimal APIs and hosted services, RabbitMQ.Client 7.2.2, RabbitMQ 4.3.6 management image, PostgreSQL 17, EF Core 10.0.11/Npgsql provider 10.0.3, xUnit 2.9.3, Docker Compose v2, ngrok v3 agent configuration.

**Spec:** `docs/superpowers/specs/2026-09-24-chat-inbox-inbound-design.md`

## Global Constraints

- The PR2 boundary is text-only inbound Telegram, persistence, and local read-only queries; no operator auth, media, edits, outbound replies, SignalR, or Angular inbox behavior.
- `ChatInbox.Api` is the only executable host; `Application` owns use cases/contracts, `Infrastructure` adapters, and `Domain` remains framework-free.
- Require `X-Telegram-Bot-Api-Secret-Token`; request body limit is 64 KiB; text limit is 4,096 Unicode characters; Telegram IDs use 64-bit integers.
- Return `200` only after a mandatory, persistent, routable, positively confirmed broker publish; broker uncertainty returns `5xx`.
- PostgreSQL is authoritative: one transaction covers processed marker, conversation upsert, message insert, and summary update; consumer ack follows commit.
- One durable direct exchange, durable inbound quorum queue, fixed routing key, delivery limit 5, delayed retry `all` with 1,000 ms minimum and 30,000 ms maximum, and durable DLX/DLQ.
- `GET` routes are local verification only: map API host port as `127.0.0.1:8080:8080`; public ngrok traffic policy permits only `POST /webhooks/telegram`.
- Plain `docker compose up` with valid `.env` discovers exactly one HTTPS ngrok URL forwarding to `api:8080` and calls `setWebhook` automatically with `drop_pending_updates=false`, the secret, and `allowed_updates=["message"]`.
- Do not put secrets in source, logs, queued envelopes, snapshots, or automated tests. Live Telegram/ngrok smoke testing is optional and separately reported.
- Approx. 400 authored changed lines per task is a planning heuristic only. Do not truncate correctness or split a coherent behavior merely to fit it.

## Review Focus

1. Authenticated older/unsupported Telegram update must return `200` without enqueue or conversation creation (Task 1 test).
2. An unroutable mandatory publish can receive a broker confirm but must still return `5xx` (Task 2 integration test).
3. Concurrent duplicate `update_id` and crash-after-commit redelivery must persist one message and acknowledge the duplicate (Tasks 3–4 tests).
4. Equal timestamps and late messages must not skip/reorder pages or regress conversation activity (Tasks 3 and 5 tests).
5. Zero/multiple ngrok candidates or a tunnel URL change must not silently register the wrong URL or declare readiness (Task 6 tests).

---

## File map and shared contracts

Existing `src/backend/ChatInbox.slnx` includes four empty .NET 10 projects. Create `src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj` and add it to the solution. All abbreviated `ChatInbox.*` paths below are relative to `src/backend/`. Keep each source file focused; proposed ownership below is exclusive to its task. Files not in this map are out of scope unless an executor records why the spec requires them before editing.

| Task | Files owned | Responsibility |
| --- | --- | --- |
| 1 | `ChatInbox.Application/Inbound/InboundTelegramUpdate.cs`, `IInboundPublisher.cs`, `ChatInbox.Infrastructure/Telegram/TelegramOptions.cs`, `ChatInbox.Api/TelegramWebhook.cs`, `ChatInbox.Api/Program.cs`, tests `WebhookTests.cs` | Transport authentication, validation/mapping, publish response semantics |
| 2 | `ChatInbox.Infrastructure/Messaging/RabbitTopology.cs`, `RabbitInboundPublisher.cs`, `ChatInbox.Infrastructure.csproj`, tests `RabbitPublisherTests.cs`, `Integration/BrokerFixture.cs` | Durable topology and confirmed mandatory publication |
| 3 | `ChatInbox.Infrastructure/Persistence/InboxDbContext.cs`, `InboxRepository.cs`, `Migrations/*`, application `Inbound/IInboundStore.cs`, tests `InboxStoreTests.cs`, `Integration/PostgresFixture.cs`, `.config/dotnet-tools.json` | Atomic, idempotent persistence and pinned migration tool |
| 4 | `ChatInbox.Infrastructure/Messaging/InboundConsumer.cs`, tests `InboundConsumerTests.cs` | Same-host manual-ack consume, retry/DLQ disposition |
| 5 | `ChatInbox.Application/Queries/InboxQueries.cs`, `ChatInbox.Infrastructure/Persistence/InboxQueries.cs`, `ChatInbox.Api/InboxEndpoints.cs`, tests `InboxQueryTests.cs` | Stable keyset read API |
| 6 | `ChatInbox.Infrastructure/Telegram/TelegramRegistration.cs`, `ChatInbox.Api/Readiness.cs`, tests `TelegramRegistrationTests.cs` | ngrok URL selection, Telegram registration/reconciliation, readiness |
| 7 | `docker-compose.yml`, `config/ngrok.yml`, `config/ngrok-policy.yml`, `.env.example`, `ChatInbox.Api/appsettings.json`, tests `ComposeSecurityTests.cs` | Five-service boot and exposure controls |
| 8 | `README.md`, `src/backend/ChatInbox.Tests/Integration/InboundFlowTests.cs`, `Integration/StackFixture.cs`, `ChatInbox.slnx` | Integration evidence and operator instructions |

Pin package versions in project files rather than allowing floating restore: `Microsoft.EntityFrameworkCore`/`.Design` 10.0.11, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `RabbitMQ.Client` 7.2.2, `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, `Microsoft.NET.Test.Sdk` 18.10.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5, `Testcontainers.PostgreSql`/`Testcontainers.RabbitMq` 4.15.0. NuGet package pages and [Testcontainers modules](https://dotnet.testcontainers.org/modules/) document these versioned packages and constructors; verify restore compatibility before implementation. Keep `dotnet-ef` 10.0.11 in repository-local `.config/dotnet-tools.json`, not a developer's global tools.

The intended application-facing signatures are:

```csharp
public sealed record InboundTelegramUpdate(int Version, long UpdateId, long ChatId,
    long MessageId, DateTimeOffset SentAt, string Text, long? SenderId,
    string? SenderFirstName, string? SenderLastName, string? ChatTitle);
public interface IInboundPublisher
{
    Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken);
}
public interface IInboundStore
{
    Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken);
}
public enum StoreOutcome { Inserted, AlreadyProcessed }
```

Task-local test infrastructure is explicit rather than implicit: Task 1's `WebhookTests.cs` defines `RecordingPublisher` and `WebhookFactory`; Task 2's `Integration/BrokerFixture.cs` defines a RabbitMQ `IAsyncLifetime` fixture; Task 3's `Integration/PostgresFixture.cs` defines a PostgreSQL `IAsyncLifetime` fixture and creates a fresh database per test class; Task 8's `Integration/StackFixture.cs` composes those two fixtures and the real `WebApplicationFactory`. In all container-dependent tests, start the fixture before the RED assertion. If `StartAsync` fails because Docker is inaccessible, the run is **environment-blocked**, not an expected TDD RED or a skipped/passing test. Never catch Docker startup failure and convert it to `Skip` or success.

```csharp
// ChatInbox.Tests/Integration/BrokerFixture.cs (Task 2)
public sealed class BrokerFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder("rabbitmq:4.3.6-management").Build();
    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
    public string AmqpUri => Container.GetConnectionString();
    public Uri ManagementUri => new($"http://{Container.Hostname}:{Container.GetMappedPublicPort(15672)}/");
}

// ChatInbox.Tests/Integration/PostgresFixture.cs (Task 3)
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
    public string ConnectionString => Container.GetConnectionString();
}
```

Use `IClassFixture<BrokerFixture>` and/or `IClassFixture<PostgresFixture>` as needed. `RabbitMqContainer`, `RabbitMqBuilder`, `PostgreSqlContainer`, and `PostgreSqlBuilder` come from the pinned Testcontainers packages. A fixture's startup must run successfully **before** asserting that the product behavior is missing; a compile error for a missing production type is also a legitimate RED once package restore succeeded.

`RabbitInboundPublisher` treats return, nack, timeout, and connection loss as failure even when their outcome is uncertain; the caller must not claim durable persistence. `InboxRepository.StoreAsync` reports `AlreadyProcessed` only after the winning committed transaction is observed. Do not use a process-local duplicate cache.

## Task 1: Authenticated, bounded webhook intake

**Files:** Create the Task 1 files in the file map; modify `ChatInbox.Api/ChatInbox.Api.csproj` only to reference the testable endpoint assembly contract. Create `ChatInbox.Tests/ChatInbox.Tests.csproj` with the exact pinned test package versions in the file map's package paragraph; add to solution.

**Interfaces:** Consumes the shared `IInboundPublisher`; produces `TelegramWebhook.MapTelegramWebhook(WebApplication app)`, infrastructure-owned `TelegramOptions(string BotToken, string WebhookSecret)` (registered as validated options), and the version-1 `InboundTelegramUpdate` record. Inject a fake publisher in component tests; no real token.

- [ ] **Step 1 — RED:** Write component tests with `WebApplicationFactory<Program>` replacing `IInboundPublisher` with a recording fake. Send the JSON below with configured test secret `test_secret_123`; assert `200`, exactly one version-1 envelope, `long` IDs, and no credential fields. Repeat with no/wrong secret and assert `401` and zero publishes; use valid JSON `{"update_id":123,"edited_message":{}}` to assert `200` and zero publishes; use 4,097 Unicode scalar values to assert `400`; send malformed JSON, missing `update_id`, wrong content type, and 65,537 bytes to assert `400`, `400`, `415`, and `413` respectively.

```csharp
// WebhookTests.cs; Program.cs adds: public partial class Program { }
public sealed class RecordingPublisher : IInboundPublisher
{
    public List<InboundTelegramUpdate> Published { get; } = [];
    public Task PublishConfirmedAsync(InboundTelegramUpdate item, CancellationToken ct)
    { Published.Add(item); return Task.CompletedTask; }
}
public sealed class WebhookFactory : WebApplicationFactory<Program>
{
    public RecordingPublisher Publisher { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")
        .UseSetting("Telegram:WebhookSecret", "test_secret_123")
        .ConfigureTestServices(s => s.AddSingleton<IInboundPublisher>(Publisher));
}
[Fact]
public async Task WrongSecretNeverPublishes()
{
    await using var factory = new WebhookFactory();
    using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/telegram")
    { Content = JsonContent.Create(new { update_id = 123L, message = new { text = "hi" } }) };
    using var response = await factory.CreateClient().SendAsync(request);
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    Assert.Empty(factory.Publisher.Published);
}
```

```json
{"update_id":2147483648,"message":{"message_id":2147483649,"date":1700000000,"chat":{"id":-2147483650,"title":"Test"},"from":{"id":2147483651,"first_name":"Ada"},"text":"hello"}}
```

- [ ] **Step 2 — verify RED:** Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~WebhookTests`; expect failing assertions because route and mapper are absent, not a fixture/configuration error.
- [ ] **Step 3 — GREEN:** Add strongly validated Telegram options (token and distinct allowed-character 1–256-character secret) without printing values. Compare UTF-8 bytes using `CryptographicOperations.FixedTimeEquals` after equal-length check. Reject before parsing/publishing on absent or wrong secret. Limit body with endpoint request-size metadata and a bounded read; require JSON media type; parse with `JsonDocument`, `TryGetInt64`, and only `message.text`. Count Unicode scalars with `EnumerateRunes().Count()`. Ignore other well-formed update kinds with a safe counter. Route maps application exceptions from broker publish to `503` without logging body/secret; do not turn cancellation into success.

```csharp
// TelegramWebhook.cs: authentication and versioned mapping before IInboundPublisher call
static bool HasValidSecret(HttpRequest request, string expected)
{
    if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var supplied))
        return false;
    var left = Encoding.UTF8.GetBytes(supplied.ToString());
    var right = Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
static InboundTelegramUpdate MapText(JsonElement root)
{
    var message = root.GetProperty("message");
    var text = message.GetProperty("text").GetString()!;
    if (string.IsNullOrWhiteSpace(text) || text.EnumerateRunes().Count() > 4096)
        throw new BadHttpRequestException("Invalid text", 400);
    return new(1, root.GetProperty("update_id").GetInt64(),
        message.GetProperty("chat").GetProperty("id").GetInt64(),
        message.GetProperty("message_id").GetInt64(),
        DateTimeOffset.FromUnixTimeSeconds(message.GetProperty("date").GetInt64()),
        text, null, null, null, null);
}
```
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run the filtered test, then `dotnet test src/backend/ChatInbox.slnx`. Extract only repeated JSON/response helpers in tests; rerun both. Do not log whole `HttpRequest` or `JsonDocument`.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Application/Inbound src/backend/ChatInbox.Infrastructure/Telegram/TelegramOptions.cs src/backend/ChatInbox.Api src/backend/ChatInbox.Tests src/backend/ChatInbox.slnx && git commit -m "feat: validate and map Telegram webhooks"`.

## Task 2: Broker topology and confirmed publication

**Files:** Task 2 messaging files and tests; modify `ChatInbox.Api/Program.cs` only for DI and topology startup ordering. Preserve Task 1 route contract.

**Interfaces:** Implement `IInboundPublisher.PublishConfirmedAsync`; `RabbitInboundPublisher` also implements `IAsyncDisposable` and exposes `ConnectAsync(string amqpUri, string exchange, TimeSpan timeout) : Task<RabbitInboundPublisher>` for test construction. Expose `RabbitTopology.DeclareAsync(IChannel channel, CancellationToken)`. `RabbitTopology` defines `InboundExchange = "chat-inbox.inbound"`, `InboundQueue = "chat-inbox.inbound.q"`, `InboundRoutingKey = "chat-inbox.inbound"`, `DeadExchange = "chat-inbox.dead"`, and `DeadQueue = "chat-inbox.dead.q"`. The publisher owns a dedicated channel or serialized channel access; it must not share an `IChannel` concurrently with the consumer.

- [ ] **Step 1 — RED:** Start `BrokerFixture` directly (it pins RabbitMQ 4.3.6 independently of Task 7's still-floating Compose image). Add a RabbitMQ-container integration test: declare topology, publish one envelope, consume its exact `UpdateId`, and assert persistent delivery. Remove the binding on an isolated exchange in a second test and assert `PublishConfirmedAsync` throws despite a broker confirm. Close broker connection before confirmation in a third test and assert failure/uncertainty, never success. Use a bounded 5-second wait; do not use Telegram credentials.

```csharp
// RabbitPublisherTests.cs; isolated exchange is declared with no queue binding
public sealed class RabbitPublisherTests(BrokerFixture broker) : IClassFixture<BrokerFixture>
{
    [Fact]
    public async Task MandatoryUnroutablePublishFailsDespiteConfirm()
    {
        var connectionFactory = new ConnectionFactory { Uri = new Uri(broker.AmqpUri) };
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync("test.unbound", type: "direct", durable: true,
            autoDelete: false); // deliberately no QueueBindAsync
        var publisher = await RabbitInboundPublisher.ConnectAsync(
            broker.AmqpUri, exchange: "test.unbound", timeout: TimeSpan.FromSeconds(5));
        await using (publisher)
            await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishConfirmedAsync(
                new(1, 7, 8, 9, DateTimeOffset.UtcNow, "hi", null, null, null, null),
                CancellationToken.None));
    }
}
```
- [ ] **Step 2 — verify RED:** Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~RabbitPublisherTests`; expect failure for missing publisher/topology. If Docker is unavailable, record the integration test as blocked, not green.
- [ ] **Step 3 — GREEN:** Use RabbitMQ .NET client 7 `CreateChannelOptions` with publisher confirmation tracking enabled and `BasicPublishAsync(exchange, routingKey, mandatory: true, basicProperties, body)` with `DeliveryMode=2`, `ContentType="application/json"`, version header. Await confirmation with a bounded cancellation deadline. Attach `BasicReturn` handling before publication and correlate return to the in-flight message; serialize publication on that channel so correlation is unambiguous. Declare durable direct exchange/queue/binding, inbound `x-queue-type=quorum`, `x-delivery-limit=5`, `x-delayed-retry-type=all`, `x-delayed-retry-min=1000`, `x-delayed-retry-max=30000`, dead-letter exchange/routing key, and durable DLQ. Fail startup on inequivalent preexisting topology rather than silently using it. Keep connection settings out of logs.

```csharp
// RabbitTopology.cs: exact queue arguments; declare DLX/DLQ before inbound queue
var arguments = new Dictionary<string, object?>
{
    ["x-queue-type"] = "quorum", ["x-delivery-limit"] = 5,
    ["x-delayed-retry-type"] = "all", ["x-delayed-retry-min"] = 1000,
    ["x-delayed-retry-max"] = 30000,
    ["x-dead-letter-exchange"] = "chat-inbox.dead",
    ["x-dead-letter-routing-key"] = "chat-inbox.dead"
};
await channel.ExchangeDeclareAsync("chat-inbox.dead", type: "direct", durable: true,
    autoDelete: false, cancellationToken: ct);
await channel.QueueDeclareAsync("chat-inbox.dead.q", durable: true,
    exclusive: false, autoDelete: false, cancellationToken: ct);
await channel.QueueBindAsync("chat-inbox.dead.q", "chat-inbox.dead", "chat-inbox.dead",
    cancellationToken: ct);
await channel.QueueDeclareAsync("chat-inbox.inbound.q", durable: true,
    exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: ct);
await channel.ExchangeDeclareAsync("chat-inbox.inbound", type: "direct", durable: true,
    autoDelete: false, cancellationToken: ct);
await channel.QueueBindAsync("chat-inbox.inbound.q", "chat-inbox.inbound",
    "chat-inbox.inbound", cancellationToken: ct);
// RabbitInboundPublisher.cs: one serialized in-flight publish per channel
await _publishGate.WaitAsync(ct);
bool returnedForThisPublication = false;
void OnReturn(object? sender, BasicReturnEventArgs args) => returnedForThisPublication = true;
channel.BasicReturn += OnReturn;
try {
using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
deadline.CancelAfter(TimeSpan.FromSeconds(5));
var properties = new BasicProperties { ContentType = "application/json", DeliveryMode = 2 };
await channel.BasicPublishAsync("chat-inbox.inbound", "chat-inbox.inbound",
    mandatory: true, basicProperties: properties,
    body: JsonSerializer.SerializeToUtf8Bytes(update), cancellationToken: deadline.Token);
if (returnedForThisPublication) throw new IOException("Broker returned unroutable update");
} finally { channel.BasicReturn -= OnReturn; _publishGate.Release(); }
```
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered test and full `dotnet test src/backend/ChatInbox.slnx`; inspect `rabbitmq-diagnostics`/management in the pinned image to verify arguments and binding. Separate serialization from AMQP code if it improves clarity; rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Infrastructure src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: confirm durable RabbitMQ intake"`.

RabbitMQ's official [client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) and [confirm tutorial](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet) describe 7.x `BasicPublishAsync`, mandatory returns, and tracked confirms. The integration test, not a fake, establishes that this exact client/broker combination treats a returned message as failed.

## Task 3: Transactional idempotent PostgreSQL store

**Files:** Task 3 persistence/application files and tests; add the exact pinned EF Core 10.0.11, Npgsql provider 10.0.3, and EF Design 10.0.11 package versions from the file map's package paragraph. `Migrations/*` is generated from the committed model, not handwritten drift.

**Interfaces:** Implement `IInboundStore.StoreAsync`; return `Inserted` or `AlreadyProcessed`. `InboxDbContext(DbContextOptions<InboxDbContext>)` and `InboxRepository(InboxDbContext)` are the testable infrastructure constructors; scope one DbContext per concurrent call. No database type leaks into `Application`.

```csharp
// InboxDbContext.cs; EF entities are infrastructure-owned, not API contracts
public sealed class Conversation
{
    public Guid Id { get; set; }
    public long TelegramChatId { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public long? LastTelegramMessageId { get; set; }
    public string? LastMessagePreview { get; set; }
}
public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long TelegramMessageId { get; set; }
    public long TelegramUpdateId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public string Direction { get; set; } = "inbound";
}
public sealed class ProcessedUpdate
{
    public long UpdateId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
public sealed class InboxDbContext(DbContextOptions<InboxDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ProcessedUpdate> ProcessedUpdates => Set<ProcessedUpdate>();
    // Put the fluent mappings below in OnModelCreating.
}
```

- [ ] **Step 1 — RED:** Start `PostgresFixture` and create a uniquely named database (or schema + dedicated `search_path`) for this test class; once the migration exists, apply it before behavior assertions. Add real PostgreSQL integration tests: store a 64-bit-ID message and assert one processed marker, conversation, and inbound message; run 16 concurrent `StoreAsync` calls with the same `UpdateId` and assert one message and 15 `AlreadyProcessed`; force a message unique-key collision with a **different** update ID after marker insertion and assert the new marker is absent after rollback; send an older `SentAt` after a newer message and assert `last_message_at` and `last_message_preview` remain newer. Docker/image connection failure is environment-blocked, **not** RED. Missing store/schema/migration after healthy Docker is expected product RED; unrelated migration credential/setup failure is blocked.

```csharp
// InboxStoreTests.cs
[Fact]
public async Task LateMessageDoesNotRegressConversationSummary()
{
    var store = BuildStore(_postgres.ConnectionString);
    var newer = new InboundTelegramUpdate(1, 101, 900, 51,
        DateTimeOffset.Parse("2026-09-24T10:00:00Z"), "new", null, null, null, null);
    var older = newer with { UpdateId = 102, MessageId = 50,
        SentAt = newer.SentAt.AddMinutes(-1), Text = "old" };
    Assert.Equal(StoreOutcome.Inserted, await store.StoreAsync(newer, CancellationToken.None));
    Assert.Equal(StoreOutcome.Inserted, await store.StoreAsync(older, CancellationToken.None));
    var row = await ReadConversationAsync(_postgres.ConnectionString, 900);
    Assert.Equal(newer.SentAt, row.LastMessageAt);
    Assert.Equal("new", row.LastMessagePreview);
}
private static IInboundStore BuildStore(string connectionString)
{
    var options = new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(connectionString).Options;
    return new InboxRepository(new InboxDbContext(options));
}
private static async Task<(DateTimeOffset? LastMessageAt, string? LastMessagePreview)>
    ReadConversationAsync(string connectionString, long chatId)
{
    var options = new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(connectionString).Options;
    await using var db = new InboxDbContext(options);
    var row = await db.Conversations.AsNoTracking()
        .SingleAsync(x => x.TelegramChatId == chatId);
    return (row.LastMessageAt, row.LastMessagePreview);
}
```
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~InboxStoreTests`; expected failure is missing store/schema, not unavailable Docker.
- [ ] **Step 3 — GREEN:** Map `conversations`, `messages`, `processed_updates` with UUID PKs, bigint Telegram IDs, required text/direction/timestamps, unique chat ID, unique update ID, unique `(conversation_id,telegram_message_id)`, FK, and indexes `(last_message_at DESC,id DESC)` and `(conversation_id,sent_at,id)`. `conversations.last_message_preview` and `last_telegram_message_id` are required summary columns (nullable before the first message); update both with `last_message_at` only when `(SentAt, MessageId)` wins the deterministic summary comparison, so the three fields never diverge. In one transaction insert marker, upsert conversation by `telegram_chat_id`, insert message, update display/summary only for that later pair. On PostgreSQL unique violation for `processed_updates.update_id`, roll back and query that marker in a fresh transaction; return duplicate success only if committed marker exists. Re-throw any other unique/DB failure. Apply migrations at startup before consumer/readiness, fail visibly if unavailable.

```csharp
// InboxDbContext.cs: entity configuration core
modelBuilder.Entity<Conversation>(b => {
    b.ToTable("conversations");
    b.HasKey(x => x.Id);
    b.HasIndex(x => x.TelegramChatId).IsUnique();
    b.Property(x => x.LastMessagePreview).HasColumnName("last_message_preview");
    b.HasIndex(x => new { x.LastMessageAt, x.Id }).IsDescending();
});
modelBuilder.Entity<ProcessedUpdate>(b => {
    b.ToTable("processed_updates"); b.HasKey(x => x.UpdateId);
});
// InboxRepository.cs: both summary fields change under the same later-message guard
if (conversation.LastMessageAt is null ||
    (update.SentAt, update.MessageId).CompareTo(
        (conversation.LastMessageAt.Value, conversation.LastTelegramMessageId ?? long.MinValue)) > 0)
{
    conversation.LastMessageAt = update.SentAt;
    conversation.LastTelegramMessageId = update.MessageId;
    conversation.LastMessagePreview = update.Text;
}
```

Before generating migration, create/commit repository-local tool manifest and restore its exact version:

```bash
dotnet new tool-manifest --force
dotnet tool install dotnet-ef --version 10.0.11
dotnet tool restore
dotnet tool run dotnet-ef --version # must print 10.0.11
dotnet tool run dotnet-ef migrations add InitialInbox --project src/backend/ChatInbox.Infrastructure --startup-project src/backend/ChatInbox.Api --output-dir Persistence/Migrations
```
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered and full solution tests, then `dotnet tool run dotnet-ef migrations script --idempotent --project src/backend/ChatInbox.Infrastructure --startup-project src/backend/ChatInbox.Api` to inspect SQL. Refactor transaction helper only after green and rerun.
- [ ] **Step 5 — commit:** `git add .config/dotnet-tools.json src/backend/ChatInbox.Application/Inbound/IInboundStore.cs src/backend/ChatInbox.Infrastructure/Persistence src/backend/ChatInbox.Infrastructure/ChatInbox.Infrastructure.csproj src/backend/ChatInbox.Tests src/backend/ChatInbox.Api/Program.cs && git commit -m "feat: persist Telegram updates idempotently"`.

Do not claim cross-process exactly-once. The unique constraint and post-conflict committed-marker check, not an in-memory lock, establish the duplicate outcome.

## Task 4: Same-host consumer with bounded retry and DLQ

**Files:** Task 4 consumer/test files; modify `ChatInbox.Api/Program.cs` only for hosted-service registration and readiness integration.

**Interfaces:** `InboundConsumer : BackgroundService` is constructed from `IConnection`, `IServiceScopeFactory`, and `ILogger<InboundConsumer>`; it consumes version-1 envelopes and calls `IInboundStore.StoreAsync`. It owns a separate consumer channel and `BasicQosAsync(prefetchCount: 1)`; one scoped store transaction per delivery.

- [ ] **Step 1 — RED:** Start both `BrokerFixture` and `PostgresFixture`, migrate the isolated PostgreSQL database, and declare Task 2 topology on the pinned RabbitMQ fixture; fixture startup or migration failure is environment-blocked, not RED. Publish a valid envelope, wait until stored, and assert queue delivery is acknowledged only after commit. Publish the same update again and assert one message and drained queue. Force a transient store failure, assert no positive ack and subsequent delivery; restore DB before fifth failure and assert one store. Publish malformed version/JSON and assert it appears in DLQ. Force five `basic.reject(requeue=true)` failures and assert DLQ presence with `x-death`/delivery diagnostic headers; this last test is a required pinned-image contract check. RED means a behavior assertion fails after both dependencies are healthy.

```csharp
// InboundConsumerTests.cs; RecordingStore blocks commit until released
[Fact]
public async Task DeliveryIsNotAckedBeforeStoreCommit()
{
    var store = new BlockingStore();
    await using var consumer = await StartConsumerAsync(_broker.AmqpUri, store);
    await PublishAsync(_broker.AmqpUri, Update(205));
    await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal(1, await UnackedCountAsync(_broker, RabbitTopology.InboundQueue));
    store.Release.SetResult();
    await WaitUntilAsync(async () => await UnackedCountAsync(
        _broker, RabbitTopology.InboundQueue) == 0, TimeSpan.FromSeconds(5));
}
private sealed class BlockingStore : IInboundStore
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken ct)
    {
        Entered.SetResult();
        await Release.Task.WaitAsync(ct);
        return StoreOutcome.Inserted;
    }
}
private static InboundTelegramUpdate Update(long id) => new(1, id, 80, id,
    DateTimeOffset.Parse("2026-09-24T10:00:00Z"), "test", null, null, null, null);
private static async Task PublishAsync(string uri, InboundTelegramUpdate update)
{
    await using var publisher = await RabbitInboundPublisher.ConnectAsync(
        uri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
    await publisher.PublishConfirmedAsync(update, CancellationToken.None);
}
private static async Task<ConsumerHarness> StartConsumerAsync(string uri, IInboundStore store)
{
    var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync();
    var host = Host.CreateDefaultBuilder().ConfigureServices(services => {
        services.AddSingleton(connection);
        services.AddSingleton(store);
        services.AddHostedService<InboundConsumer>();
    }).Build();
    await host.StartAsync();
    return new ConsumerHarness(host, connection);
}
private sealed class ConsumerHarness(IHost host, IConnection connection) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    { await host.StopAsync(); host.Dispose(); await connection.DisposeAsync(); }
}
private static async Task<int> UnackedCountAsync(BrokerFixture broker, string queue)
{
    using var client = new HttpClient { BaseAddress = broker.ManagementUri };
    client.DefaultRequestHeaders.Authorization = new("Basic",
        Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
    using var response = await client.GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}");
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("messages_unacknowledged").GetInt32();
}
private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
{
    using var deadline = new CancellationTokenSource(timeout);
    while (!await predicate())
    {
        deadline.Token.ThrowIfCancellationRequested();
        await Task.Delay(50, deadline.Token);
    }
}
```
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~InboundConsumerTests`; expected missing consumer/disposition behavior. If the image does not count rejection toward limit, stop and revise the design with the human; do not replace bounded retries with an unbounded loop.
- [ ] **Step 3 — GREEN:** Start consumer after migration/topology readiness; deserialize strictly by `Version`. Use manual acknowledgements: `BasicAckAsync` only after `Inserted` or `AlreadyProcessed` returns; `BasicRejectAsync(requeue: true)` on transient persistence failures; `BasicRejectAsync(requeue: false)` on malformed/unsupported envelopes. Prefetch 1, one consumer channel, cancellation-aware shutdown. Log `UpdateId` and failure category only; expose processing-failure counter. If a channel closes before ack, allow RabbitMQ to redeliver; never issue a synthetic success. Do not republish from consumer.

```csharp
// InboundConsumer.cs: callback core; channel created with autoAck: false, prefetch 1
try
{
    var update = JsonSerializer.Deserialize<InboundTelegramUpdate>(args.Body.Span);
    if (update is null || update.Version != 1) throw new JsonException("Unsupported envelope");
    using var scope = services.CreateScope();
    var outcome = await scope.ServiceProvider.GetRequiredService<IInboundStore>()
        .StoreAsync(update, cancellationToken);
    await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: cancellationToken);
}
catch (JsonException)
{
    await channel.BasicRejectAsync(args.DeliveryTag, requeue: false, cancellationToken: cancellationToken);
}
catch (Exception) when (!cancellationToken.IsCancellationRequested)
{
    await channel.BasicRejectAsync(args.DeliveryTag, requeue: true, cancellationToken: cancellationToken);
}
```
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered and full solution tests. On actual `rabbitmq:4.3.6-management`, observe retry delay, delivery count, and dead-lettering; report exact result separately from unit tests. Refactor disposition classification only after green, then rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Infrastructure/Messaging/InboundConsumer.cs src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: consume inbound updates with bounded retries"`.

Official [RabbitMQ 4.3 quorum queue docs](https://www.rabbitmq.com/docs/quorum-queues) state delayed retry `all` and that AMQP 0.9.1 `basic.reject`, unlike `basic.nack`, increments delivery count; image-level testing remains mandatory.

## Task 5: Stable local read queries

**Files:** Task 5 query/endpoint/test files; modify `ChatInbox.Api/Program.cs` only to map routes.

**Interfaces:** `IInboxQueries.ListConversationsAsync(int limit, PageCursor? before, CancellationToken)` and `ListMessagesAsync(Guid conversationId, int limit, PageCursor? before, CancellationToken)` return `Page<T>` (`IReadOnlyList<T> Items`, `string? NextCursor`). `PageCursor` is `(DateTimeOffset Timestamp, Guid Id)` encoded as opaque base64url JSON with version 1; parser returns a typed invalid result, never throws to endpoint. `ConversationDto` has `Id`, `TelegramChatId`, `DisplayName`, `LastMessageAt`, `LastMessagePreview`; `MessageDto` has `Id`, `TelegramMessageId`, `Direction`, `Text`, `SentAt`.

```csharp
// ChatInbox.Application/Queries/InboxQueries.cs
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record ConversationDto(Guid Id, long TelegramChatId, string? DisplayName,
    DateTimeOffset? LastMessageAt, string? LastMessagePreview);
public sealed record MessageDto(Guid Id, long TelegramMessageId, string Direction,
    string Text, DateTimeOffset SentAt);
```

- [ ] **Step 1 — RED:** Seed two conversations with equal `last_message_at` and three messages with equal `sent_at`; request `limit=1` pages until exhausted and assert no duplicate/skip, newest conversations first, newest message page selected but each returned page chronological. Assert default 50, accepted 1/100, rejected 0/101/non-numeric and tampered cursor (`400`), unknown conversation (`404`). Include late message test showing conversation remains ordered by newest message. Assert no GET mutates row counts.

```csharp
// InboxQueryTests.cs
public sealed class InboxQueryTests(PostgresFixture _postgres, BrokerFixture _broker)
    : IClassFixture<PostgresFixture>, IClassFixture<BrokerFixture>, IDisposable
{
private Guid _conversationId;
private WebApplicationFactory<Program>? _factory;
[Fact]
public async Task EqualTimestampsUseIdTieBreakWithoutSkippingRows()
{
    var ids = await SeedThreeMessagesAtAsync(_postgres.ConnectionString,
        DateTimeOffset.Parse("2026-09-24T10:00:00Z"));
    using var client = CreateApiClient(_postgres.ConnectionString);
    var first = await client.GetFromJsonAsync<Page<MessageDto>>(
        $"/api/conversations/{_conversationId}/messages?limit=1");
    var second = await client.GetFromJsonAsync<Page<MessageDto>>(
        $"/api/conversations/{_conversationId}/messages?limit=1&before={Uri.EscapeDataString(first!.NextCursor!)}");
    Assert.NotEqual(first.Items[0].Id, second!.Items[0].Id);
    Assert.Contains(first.Items[0].Id, ids);
    Assert.Contains(second.Items[0].Id, ids);
}
private async Task<Guid[]> SeedThreeMessagesAtAsync(string connectionString, DateTimeOffset at)
{
    var options = new DbContextOptionsBuilder<InboxDbContext>()
        .UseNpgsql(connectionString).Options;
    await using (var migrationDb = new InboxDbContext(options))
        await migrationDb.Database.MigrateAsync();
    for (long i = 1; i <= 3; i++)
    {
        await using var db = new InboxDbContext(options);
        await new InboxRepository(db).StoreAsync(
            new(1, 500 + i, 400, 600 + i, at, $"message {i}", null, null, null, null),
            CancellationToken.None);
    }
    await using var readDb = new InboxDbContext(options);
    _conversationId = await readDb.Conversations.Where(x => x.TelegramChatId == 400)
        .Select(x => x.Id).SingleAsync();
    return await readDb.Messages.Where(x => x.ConversationId == _conversationId)
        .Select(x => x.Id).ToArrayAsync();
}
private HttpClient CreateApiClient(string connectionString)
{
    _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["ConnectionStrings:Inbox"] = connectionString,
                ["RabbitMq:Uri"] = _broker.AmqpUri,
                ["Telegram:WebhookSecret"] = "test_secret_123",
                ["Telegram:BotToken"] = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"
            })));
    return _factory.CreateClient();
}
public void Dispose() => _factory?.Dispose();
}
```
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~InboxQueryTests`; expected absent query/routes failure.
- [ ] **Step 3 — GREEN:** Query keyset `(timestamp,id) < (@timestamp,@id)` in descending selection order with `limit+1`, stable GUID tie-break, and indexes from Task 3. For messages reverse the selected page only when formatting the response. Encode/decode cursors with version, timestamp, id and strict length/format bounds. Reject invalid limits/cursors, and check conversation existence before empty-page response. No write-side calls from endpoints.

```csharp
// ChatInbox.Infrastructure/Persistence/InboxQueries.cs: select newest page, render chronologically
var query = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
if (before is { } cursor)
    query = query.Where(m => m.SentAt < cursor.Timestamp ||
        (m.SentAt == cursor.Timestamp && m.Id.CompareTo(cursor.Id) < 0));
var newest = await query.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id)
    .Take(limit + 1).ToListAsync(ct);
var hasMore = newest.Count > limit;
var selected = newest.Take(limit).ToArray();
var next = hasMore ? PageCursor.Encode(selected[^1].SentAt, selected[^1].Id) : null;
return new Page<MessageDto>(selected.Reverse().Select(m => new MessageDto(
    m.Id, m.TelegramMessageId, m.Direction, m.Text, m.SentAt)).ToArray(), next);
```
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered and full tests; inspect PostgreSQL query plan on seeded data for the two indexes. Extract cursor parser only if clarity improves; rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Application/Queries src/backend/ChatInbox.Infrastructure/Persistence/InboxQueries.cs src/backend/ChatInbox.Api/InboxEndpoints.cs src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: expose stable local inbox queries"`.

## Task 6: Automatic, reconciled Telegram registration

**Files:** Task 6 registration/readiness/test files; modify `ChatInbox.Api/Program.cs` for typed `HttpClient`, options, and hosted service.

**Interfaces:** `NgrokTunnelSelector.SelectHttpsApiTunnel(JsonElement tunnels) : Uri` accepts exactly one HTTPS `public_url` whose tunnel config forwards to `http://api:8080`; zero or multiple matches is a typed failure. `TelegramRegistration : BackgroundService` queries `http://ngrok:4040/api/tunnels`, posts TLS-verified `https://api.telegram.org/bot{token}/setWebhook`, and queries `getWebhookInfo`; `IRegistrationStatus` exposes ready, current URL, last error category, pending count, and last Telegram delivery error without secrets. Define `INgrokTunnelClient.GetTunnelsAsync(CancellationToken) : Task<JsonDocument>`, `TelegramApiClient(HttpClient, TelegramOptions) : IRegistrationClient`, and `IRegistrationClient` with `Task SetWebhookAsync(Uri url, string secret, CancellationToken)` and `Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken)` in `ChatInbox.Infrastructure/Telegram/TelegramRegistration.cs`; `WebhookInfo` is `record WebhookInfo(string? Url, int PendingCount, string? LastError)`. Task 8 replaces only the two external interfaces.

- [ ] **Step 1 — RED:** Unit-test zero, one, and two matching HTTPS tunnels; reject a tunnel forwarding to another service. Fake `HttpMessageHandler`: verify every startup calls `setWebhook` even when `getWebhookInfo.url` already matches, with `secret_token`, `allowed_updates=["message"]`, `drop_pending_updates=false`; simulate transient failures and recovery with bounded backoff; change URL during running service and assert re-registration/not-ready during mismatch. Assert logs/status never contain bot token or secret.

```csharp
// TelegramRegistrationTests.cs; JSON resembles local agent /api/tunnels response
[Fact]
public void AmbiguousMatchingTunnelsFailClosed()
{
    using var doc = JsonDocument.Parse("""
      {"tunnels":[
        {"public_url":"https://one.ngrok.app","config":{"addr":"http://api:8080"}},
        {"public_url":"https://two.ngrok.app","config":{"addr":"http://api:8080"}}]}
      """);
    Assert.Throws<InvalidOperationException>(() =>
        NgrokTunnelSelector.SelectHttpsApiTunnel(doc.RootElement));
}
[Fact]
public async Task RegistrationNeverDropsBacklog()
{
    var handler = new RecordingTelegramHandler();
    await RegisterOnceAsync(new Uri("https://one.ngrok.app"), handler);
    using var body = JsonDocument.Parse(handler.LastSetWebhookBody);
    Assert.False(body.RootElement.GetProperty("drop_pending_updates").GetBoolean());
    Assert.Equal("message", body.RootElement.GetProperty("allowed_updates")[0].GetString());
}
private sealed class RecordingTelegramHandler : HttpMessageHandler
{
    public string LastSetWebhookBody { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/setWebhook", StringComparison.Ordinal))
            LastSetWebhookBody = await request.Content!.ReadAsStringAsync(ct);
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
    }
}
private static async Task RegisterOnceAsync(Uri publicUrl, RecordingTelegramHandler handler)
{
    using var client = new HttpClient(handler, disposeHandler: false)
    { BaseAddress = new Uri("https://api.telegram.org/") };
    var api = new TelegramApiClient(client,
        new TelegramOptions("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi", "test_secret_123"));
    await api.SetWebhookAsync(new Uri(publicUrl, "/webhooks/telegram"),
        "test_secret_123", CancellationToken.None);
}
```
- [ ] **Step 2 — verify RED:** `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~TelegramRegistrationTests`; expected missing selector/service behavior.
- [ ] **Step 3 — GREEN:** Wait until route, broker intake, migration, and consumer readiness exist. Poll ngrok agent API with a short timeout; select one URL; POST Telegram registration on every startup and on URL change, then observe `getWebhookInfo` URL/pending/errors. Bounded exponential retry (1, 2, 4, 8, 16, 30 seconds) continues while host runs; expose unready with error category after failed attempt, not a false-green health signal. Never call `deleteWebhook` on shutdown. Keep a dedicated TLS-validating client; redact token-bearing request URI from logs/diagnostics.

```csharp
// TelegramRegistration.cs: registration payload; HttpClient uses default TLS validation
var webhookUrl = new Uri(publicUrl, "/webhooks/telegram");
var payload = new {
    url = webhookUrl.ToString(), secret_token = options.WebhookSecret,
    allowed_updates = new[] { "message" }, drop_pending_updates = false
};
using var response = await telegramClient.PostAsJsonAsync(
    $"bot{options.BotToken}/setWebhook", payload, ct);
response.EnsureSuccessStatusCode();
status.MarkReadyOnlyAfterWebhookInfoMatches(webhookUrl);
// No logging of request URI: it embeds the bot token.
```
- [ ] **Step 4 — verify GREEN and REFACTOR:** Run filtered/full fake-provider tests. The actual ngrok-agent check depends on Task 7's config and is explicitly **deferred to Task 7**, not a Task 6 green criterion. Refactor status state transitions only after green; rerun.
- [ ] **Step 5 — commit:** `git add src/backend/ChatInbox.Infrastructure/Telegram src/backend/ChatInbox.Api/Readiness.cs src/backend/ChatInbox.Api/Program.cs src/backend/ChatInbox.Tests && git commit -m "feat: reconcile Telegram webhook registration"`.

## Task 7: Five-service Compose and transport exposure

**Files:** Task 7 configuration and tests. No secret-bearing `.env` is committed.

**Interfaces:** Compose keeps `api`, `web`, `postgres`, `rabbitmq`, `ngrok`. API receives validated `Telegram__BotToken`, `Telegram__WebhookSecret`, `RabbitMq__*`, and `ConnectionStrings__Inbox`; ngrok agent API is private at `ngrok:4040`. Expose local status/metrics on the API loopback mapping only; RabbitMQ management supplies queue/DLQ depths locally.

- [ ] **Step 1 — RED:** Write configuration contract tests that parse checked-in Compose/config: API maps only `127.0.0.1:8080:8080`; port 4040 is absent from host mappings; ngrok forwards `api:8080`; policy denies all except `POST /webhooks/telegram`; image is `rabbitmq:4.3.6-management`; `.env.example` has placeholders for both Telegram secrets. Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~ComposeSecurityTests` and expect missing settings failures.

```csharp
// ComposeSecurityTests.cs; FindRoot walks parent directories to docker-compose.yml
static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
    throw new FileNotFoundException("docker-compose.yml");
}
[Fact]
public void ApiPortAndAgentApiAreNotPubliclyPublished()
{
    var compose = File.ReadAllText(Path.Combine(FindRoot(), "docker-compose.yml"));
    Assert.Contains("127.0.0.1:8080:8080", compose);
    Assert.DoesNotContain("4040:4040", compose);
    Assert.Contains("rabbitmq:4.3.6-management", compose);
}
```
- [ ] **Step 2 — GREEN:** Update Compose to pin RabbitMQ, loopback-bind API, pass private environment variables, mount a checked-in ngrok v3 config, and apply ngrok Traffic Policy on the public endpoint. Configure `agent.web_addr` so `ngrok:4040` is reachable on the private Compose network, and `agent.web_allow_hosts` narrowly for `ngrok`; do not publish 4040. Use the following candidate config, with `NGROK_AUTHTOKEN` coming from the container environment rather than the file; run `ngrok config check` in the selected image and correct only documented syntax. The policy denies all methods/paths other than the webhook POST. Update `.env.example` and API options defaults without values. If the selected image rejects this config or policy, stop and revise design rather than leave public GET routes reachable.

```yaml
# config/ngrok.yml
version: 3
agent:
  web_addr: 0.0.0.0:4040
  web_allow_hosts:
    - ngrok
endpoints:
  - name: telegram-webhook
    traffic_policy_file: /etc/ngrok/ngrok-policy.yml
    upstream:
      url: http://api:8080
```

```yaml
# config/ngrok-policy.yml
on_http_request:
  - expressions:
      - "req.method != 'POST' || req.url.path != '/webhooks/telegram'"
    actions:
      - type: deny
```
- [ ] **Step 3 — verify GREEN:** Run `docker compose config --quiet` with a temporary placeholder-only env file supplied via `--env-file`; run filtered/full tests. With valid local credentials only, inspect `docker compose ps`, `ngrok config check` inside the selected image, private `http://ngrok:4040/api/tunnels` response shape (`public_url`, forward target, exactly one HTTPS candidate), ngrok policy behavior (`POST` allowed, `GET /api/conversations` denied), API loopback and LAN reachability, and private-only agent API accessibility. This is the deferred actual-ngrok check from Task 6. Report credentialed checks separately; do not display Compose rendered secrets.

```bash
tmp_env="$(mktemp)"; trap 'rm -f "$tmp_env"' EXIT
cat > "$tmp_env" <<'ENV'
POSTGRES_DB=local_test
POSTGRES_USER=local_test
POSTGRES_PASSWORD=placeholder_only
RABBITMQ_USER=local_test
RABBITMQ_PASSWORD=placeholder_only
NGROK_AUTHTOKEN=placeholder_only
TELEGRAM_BOT_TOKEN=placeholder_only
TELEGRAM_WEBHOOK_SECRET=placeholder_only
ENV
docker compose --env-file "$tmp_env" config --quiet
```
- [ ] **Step 4 — commit:** `git add docker-compose.yml config/ngrok.yml config/ngrok-policy.yml .env.example src/backend/ChatInbox.Api/appsettings.json src/backend/ChatInbox.Tests && git commit -m "chore: secure local inbound stack exposure"`.

Official [ngrok v3 agent config](https://ngrok.com/docs/agent/config/v3) is the authority for `agent.web_addr` and `agent.web_allow_hosts`; selected-image behavior and Traffic Policy admission need actual integration proof.

## Task 8: End-to-end evidence and operator guide

**Files:** Task 8 integration tests, `README.md`, and solution membership only. No Angular source changes.

**Interfaces:** Tests use local disposable PostgreSQL/RabbitMQ containers and the actual API host. Optional live smoke requires explicit local credentials, never CI secrets in code or snapshots.

- [ ] **Step 1 — RED:** `Integration/StackFixture.cs` starts `PostgresFixture` and `BrokerFixture` (pinned 17-alpine/4.3.6), creates an actual `WebApplicationFactory<Program>` with their connection strings, a test webhook secret, and fake `IRegistrationClient`/`INgrokTunnelClient` so no real Telegram/ngrok call occurs; it does **not** replace publisher, consumer, topology, migrations, or repository. The fixture must finish container startup and migration before RED. Add an integration test that sends an authenticated webhook, waits for one persisted message, queries both GET routes, repeats the update, restarts the consumer host, and asserts one message. Add broker-unavailable `5xx` and DB-outage retry/DLQ tests. Run `dotnet test src/backend/ChatInbox.Tests/ChatInbox.Tests.csproj --filter FullyQualifiedName~Integration`; RED is a failed product assertion after healthy fixtures, whereas container/restore/migration failure is environment-blocked. Keep a bounded timeout and diagnostic failure message without payload text.

```csharp
// Integration/StackFixture.cs: configuration overlay after both containers StartAsync
public sealed class StackFixture : IAsyncLifetime
{
public PostgresFixture Postgres { get; } = new();
public BrokerFixture Broker { get; } = new();
public WebApplicationFactory<Program> Factory { get; private set; } = null!;
public async Task InitializeAsync()
{
await Postgres.InitializeAsync();
await Broker.InitializeAsync();
Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
    .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
        new Dictionary<string, string?> {
            ["ConnectionStrings:Inbox"] = Postgres.ConnectionString,
            ["RabbitMq:Uri"] = Broker.AmqpUri,
            ["Telegram:WebhookSecret"] = "test_secret_123",
            ["Telegram:BotToken"] = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi"
        }))
    .ConfigureTestServices(services => {
        services.AddSingleton<IRegistrationClient, FakeRegistrationClient>();
        services.AddSingleton<INgrokTunnelClient, FakeNgrokTunnelClient>();
    }));
using var startedClient = Factory.CreateClient(); // forces migration/topology/consumer startup
}
public async Task DisposeAsync()
{
    Factory.Dispose();
    await Broker.DisposeAsync();
    await Postgres.DisposeAsync();
}
// FakeRegistrationClient and FakeNgrokTunnelClient are top-level types in this file.
}
// Integration/InboundFlowTests.cs: inspect persisted queries, not mocked publish calls
public sealed class InboundFlowTests(StackFixture _stack) : IClassFixture<StackFixture>
{
[Fact]
public async Task DuplicateWebhookIsOneVisibleMessage()
{
    using var client = _stack.Factory.CreateClient();
    using var first = await SendAuthenticatedUpdateAsync(client, updateId: 3001);
    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    await WaitForMessageAsync(client, telegramMessageId: 7001, TimeSpan.FromSeconds(10));
    using var repeated = await SendAuthenticatedUpdateAsync(client, updateId: 3001);
    Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
    Assert.Equal(1, await CountMessagesAsync(client, telegramMessageId: 7001));
}
private static async Task<HttpResponseMessage> SendAuthenticatedUpdateAsync(HttpClient client, long updateId)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/telegram") {
        Content = JsonContent.Create(new {
            update_id = updateId,
            message = new { message_id = 7001L, date = 1780000000L,
                chat = new { id = 8001L }, text = "hello" }
        })
    };
    request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "test_secret_123");
    return await client.SendAsync(request);
}
private static async Task<int> CountMessagesAsync(HttpClient client, long telegramMessageId)
{
    var conversations = await client.GetFromJsonAsync<Page<ConversationDto>>(
        "/api/conversations?limit=100");
    var conversation = conversations!.Items.SingleOrDefault(x => x.TelegramChatId == 8001L);
    if (conversation is null) return 0;
    var page = await client.GetFromJsonAsync<Page<MessageDto>>(
        $"/api/conversations/{conversation.Id}/messages?limit=100");
    return page!.Items.Count(x => x.TelegramMessageId == telegramMessageId);
}
private static async Task WaitForMessageAsync(HttpClient client, long telegramMessageId, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline) {
        if (await CountMessagesAsync(client, telegramMessageId) == 1) return;
        await Task.Delay(50);
    }
    throw new TimeoutException("Inbound message was not visible before the deadline");
}
}
```
- [ ] **Step 2 — GREEN:** Wire disposable containers and test host so production DI/topology/migrations/consumer run; replace only external Telegram registration and secrets. Make the tests deterministic through queue-state and DB-state conditions, not arbitrary sleeps. Write README commands for `.env` setup, `docker compose up --build`, local queries, readiness and broker diagnostics, optional `getWebhookInfo` verification, secret-safe troubleshooting, replay-from-DLQ procedure after fixing cause, and shutdown behavior. State that public GETs, outbound, SignalR, and Angular inbox remain out of scope.

```csharp
// Integration/StackFixture.cs: fake only the external registration clients
internal sealed class FakeRegistrationClient : IRegistrationClient
{
    private Uri? _url;
    public Task SetWebhookAsync(Uri url, string secret, CancellationToken ct)
    { _url = url; return Task.CompletedTask; }
    public Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken ct) =>
        Task.FromResult(new WebhookInfo(_url?.ToString(), 0, null));
}
internal sealed class FakeNgrokTunnelClient : INgrokTunnelClient
{
    public Task<JsonDocument> GetTunnelsAsync(CancellationToken ct) => Task.FromResult(
        JsonDocument.Parse("""{"tunnels":[{"public_url":"https://test.ngrok.app","config":{"addr":"http://api:8080"}}]}"""));
}
// WebhookInfo is the Task 6 record: (string? Url, int PendingCount, string? LastError).
```
- [ ] **Step 3 — verify GREEN and REFACTOR:** Run `dotnet test src/backend/ChatInbox.slnx`, `dotnet build src/backend/ChatInbox.slnx --no-restore`, `docker compose config --quiet` with placeholder-only env file, and `git diff --check`. Credentialed smoke, when available: `docker compose up --build`, compare `getWebhookInfo.url` with discovered HTTPS URL plus path, send a real Telegram message, query local GETs, exercise missing-secret rejection, and confirm public GET/LAN denial. Record each command/result; no credentialed smoke means explicitly **not verified live**. Do not claim an image health check proves the Telegram path.

Run this in a **fresh shell** for Task 8; no real `.env` or token is read or printed. Run `dotnet test` separately from `dotnet build` so a passing build cannot mask failed integration tests.

```bash
tmp_env="$(mktemp)"; trap 'rm -f "$tmp_env"' EXIT
cat > "$tmp_env" <<'ENV'
POSTGRES_DB=local_test
POSTGRES_USER=local_test
POSTGRES_PASSWORD=placeholder_only
RABBITMQ_USER=local_test
RABBITMQ_PASSWORD=placeholder_only
NGROK_AUTHTOKEN=placeholder_only
TELEGRAM_BOT_TOKEN=placeholder_only
TELEGRAM_WEBHOOK_SECRET=placeholder_only
ENV
docker compose --env-file "$tmp_env" config --quiet
```
- [ ] **Step 4 — commit:** `git add README.md src/backend/ChatInbox.Tests/Integration src/backend/ChatInbox.slnx && git commit -m "test: cover inbound Telegram vertical slice"`.

## Delivery, rollback, and evidence boundary

Each task is independently reviewable and commits only its owned files. Never run multiple writers against `Program.cs` at once. Source-mutating formatters run before final verification/review; do not change candidate bytes afterward without rerunning checks. The final branch review must inspect secret exposure, AMQP return/confirm races, DB duplicate races, cursor stability, and public routing. Do not push, open a PR, deploy, or enable a real webhook merely because tests pass; those are separate delivery actions.

Rollback the application change by reverting PR2 and restoring the prior Compose image/config; **do not** automatically drop PostgreSQL tables, purge RabbitMQ queues/DLQ, or delete Telegram's webhook, since those hold durable state/backlog. If a real registration has happened, explicitly coordinate the prior webhook URL or `deleteWebhook` with the operator before shutting down the replacement. Database migration compatibility and queue topology changes require a rehearsed local rollback on a copied data set before production use. A failed live smoke or DLQ entry is not persisted success.

Evidence tiers: (1) unit/component fakes, (2) real PostgreSQL/RabbitMQ container integration including 4.3 retry/return semantics, (3) ngrok image/config and Compose security checks, (4) optional credentialed Telegram delivery. Report pass/fail/blocked separately and never promote a lower tier into a higher one.

## Plan self-review

- Spec coverage: webhook limits/auth/unsupported updates → Task 1; confirmed publication/topology → Task 2; idempotent transaction and `last_message_preview`/timestamp tie-break → Task 3; consumer retry/DLQ → Task 4; pagination → Task 5; registration/reconciliation → Task 6; private exposure/config and actual ngrok-image contract → Task 7; full-path evidence and README → Task 8.
- Every Task 1–8 RED and GREEN code step now includes a concrete task-local C# or configuration example; container fixtures have pinned images and environment failure is never counted as a valid RED. The repository-local EF tool manifest and exact package versions remove machine-global migration assumptions.
- The 12 named test helpers used in example calls are defined in their owning task's test file; Task 2's unbound exchange is explicitly declared without a binding, and Task 1's factory supplies both mandatory Telegram options. `Application` query DTOs do not reference infrastructure entities.
- The five Review Focus conditions each have an owning RED test. No source implementation is authorized by this document alone; review the plan before execution.
- External uncertainty remains deliberately visible: RabbitMQ 4.3.6 AMQP `basic.reject` retry/dead-letter behavior, the selected ngrok image's config/Traffic Policy syntax, and whether its local API exposes a v3 endpoint through `/api/tunnels` with `public_url`/upstream details require live container checks. If either provider contract fails, stop and revise the approved design instead of silently weakening it.
