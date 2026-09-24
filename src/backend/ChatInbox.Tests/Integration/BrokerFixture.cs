using Testcontainers.RabbitMq;
using Xunit;

namespace ChatInbox.Tests.Integration;

public sealed class BrokerFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder("rabbitmq:4.3.6-management")
            .WithPortBinding(15672, true)
            .Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public string AmqpUri => Container.GetConnectionString();

    public Uri ManagementUri => new($"http://{Container.Hostname}:{Container.GetMappedPublicPort(15672)}/");
}
