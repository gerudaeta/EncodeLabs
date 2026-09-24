using ChatInbox.Api;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Telegram;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new TelegramOptions(
    builder.Configuration["Telegram:BotToken"],
    builder.Configuration["Telegram:WebhookSecret"]));
builder.Services.AddSingleton<IInboundPublisher, UnavailableInboundPublisher>();

var app = builder.Build();

app.MapTelegramWebhook();

app.Run();

public partial class Program { }
