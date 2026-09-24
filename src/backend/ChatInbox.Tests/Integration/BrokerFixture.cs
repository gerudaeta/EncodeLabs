using Testcontainers.RabbitMq;
using Xunit;

namespace ChatInbox.Tests.Integration;

public sealed class BrokerFixture : IAsyncLifetime
{
    public BrokerFixture() : this(null) { }

    internal BrokerFixture(int? fixedAmqpHostPort)
    {
        var builder = new RabbitMqBuilder("rabbitmq:4.3.6-management")
            .WithPortBinding(15672, true);
        if (fixedAmqpHostPort is int port)
            builder = builder.WithPortBinding(port, 5672);
        Container = builder.Build();
    }

    public RabbitMqContainer Container { get; }

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public string AmqpUri => Container.GetConnectionString();

    public Uri ManagementUri => new($"http://{Container.Hostname}:{Container.GetMappedPublicPort(15672)}/");
}
