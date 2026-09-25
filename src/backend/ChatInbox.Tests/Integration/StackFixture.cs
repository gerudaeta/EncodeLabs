using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChatInbox.Tests.Integration;

public sealed class StackFixture : IAsyncLifetime
{
    public const string WebhookSecret = "test_secret_123";
    public PostgresFixture Postgres { get; } = new();
    public BrokerFixture Broker { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        await Broker.InitializeAsync();
        await StartHostAsync();
    }

    public async Task RestartHostAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await StartHostAsync();
    }

    private Task StartHostAsync()
    {
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Postgres", Postgres.ConnectionString)
            .UseSetting("RabbitMQ:Uri", Broker.AmqpUri)
            .UseSetting("Telegram:WebhookSecret", WebhookSecret)
            .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")
            .UseSetting("Telegram:RegistrationEnabled", "false"));
        using var client = Factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await Broker.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}
