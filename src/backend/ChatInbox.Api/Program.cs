using ChatInbox.Api;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Telegram;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new TelegramOptions(
    builder.Configuration["Telegram:BotToken"],
    builder.Configuration["Telegram:WebhookSecret"]));
var amqpUri = builder.Configuration["RabbitMQ:Uri"];
if (string.IsNullOrWhiteSpace(amqpUri))
{
    // Until Compose supplies the broker URI, intake remains explicitly unavailable.
    builder.Services.AddSingleton<IInboundPublisher, UnavailableInboundPublisher>();
}
else
{
    var factory = new ConnectionFactory { Uri = new Uri(amqpUri), AutomaticRecoveryEnabled = false };
    await using (var topologyConnection = await factory.CreateConnectionAsync())
    await using (var topologyChannel = await topologyConnection.CreateChannelAsync())
    {
        await RabbitTopology.DeclareAsync(topologyChannel, CancellationToken.None);
    }
    var publisher = await RabbitInboundPublisher.ConnectAsync(
        amqpUri, RabbitTopology.InboundExchange, TimeSpan.FromSeconds(5));
    builder.Services.AddSingleton<IInboundPublisher>(publisher);
}

var app = builder.Build();

app.MapTelegramWebhook();

app.Run();

public partial class Program { }
