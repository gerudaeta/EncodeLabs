using ChatInbox.Api;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new TelegramOptions(
    builder.Configuration["Telegram:BotToken"],
    builder.Configuration["Telegram:WebhookSecret"]));
var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(postgresConnection))
{
    builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(postgresConnection));
    builder.Services.AddScoped<IInboundStore, InboxRepository>();
}
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

if (!string.IsNullOrWhiteSpace(postgresConnection))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<InboxDbContext>()
        .Database.MigrateAsync();
}

app.MapTelegramWebhook();

app.Run();

public partial class Program { }
