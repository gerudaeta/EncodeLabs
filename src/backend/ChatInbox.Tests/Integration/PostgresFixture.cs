using Testcontainers.PostgreSql;
using Xunit;

namespace ChatInbox.Tests.Integration;

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase($"inbox_{Guid.NewGuid():N}")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
