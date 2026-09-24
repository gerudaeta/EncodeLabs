using ChatInbox.Api;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Telegram;

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
    builder.Services.AddSingleton(new RabbitPublisherHostedService(amqpUri, TimeSpan.FromSeconds(5)));
    builder.Services.AddSingleton<IInboundPublisher>(services =>
        services.GetRequiredService<RabbitPublisherHostedService>());
    builder.Services.AddSingleton<IHostedService>(services =>
        services.GetRequiredService<RabbitPublisherHostedService>());
}

var app = builder.Build();

app.MapTelegramWebhook();

app.Run();

public partial class Program { }
