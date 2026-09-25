using ChatInbox.Api;
using ChatInbox.Api.Realtime;
using ChatInbox.Application.Inbound;
using ChatInbox.Application.Outbound;
using ChatInbox.Application.Queries;
using ChatInbox.Application.Read;
using ChatInbox.Application.Realtime;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Persistence;
using ChatInbox.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new TelegramOptions(
    builder.Configuration["Telegram:BotToken"],
    builder.Configuration["Telegram:WebhookSecret"]));
builder.Services.AddSingleton<RegistrationStatus>();
builder.Services.AddSingleton<IRegistrationStatus>(services =>
    services.GetRequiredService<RegistrationStatus>());
builder.Services.AddSignalR();
builder.Services.AddHybridCache(); // In-memory only: no distributed L2 registered.
builder.Services.AddSingleton<SignalRInboxNotifier>();
builder.Services.AddSingleton<IInboxNotifier>(services => new CacheInvalidatingInboxNotifier(
    services.GetRequiredService<HybridCache>(), services.GetRequiredService<SignalRInboxNotifier>()));
var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(postgresConnection))
{
    builder.Services.AddDbContext<InboxDbContext>(options => options.UseNpgsql(postgresConnection));
    builder.Services.AddScoped<IInboundStore, InboxRepository>();
    builder.Services.AddScoped<IInboxQueries, InboxQueries>();
    builder.Services.AddScoped<IReplyRepository, ReplyRepository>();
    builder.Services.AddScoped<IReadStateRepository, ReadStateRepository>();
    builder.Services.AddScoped<MarkConversationReadUseCase>();
    builder.Services.AddHttpClient<IReplySender, TelegramReplySender>(client =>
    {
        client.BaseAddress = new Uri("https://api.telegram.org/");
        client.Timeout = TimeSpan.FromSeconds(10);
    }).RemoveAllLoggers(); // The bot token is part of the request path.
    builder.Services.AddScoped<SendReplyUseCase>();
}
var amqpUri = builder.Configuration["RabbitMQ:Uri"];
IConnection? consumerConnection = null;
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
    if (!string.IsNullOrWhiteSpace(postgresConnection))
    {
        consumerConnection = await new ConnectionFactory
        {
            Uri = new Uri(amqpUri), AutomaticRecoveryEnabled = false
        }
            .CreateConnectionAsync();
        builder.Services.AddSingleton(consumerConnection);
        builder.Services.AddSingleton<InboundConsumer>();
        builder.Services.AddSingleton<IInboundConsumerReadiness>(services =>
            services.GetRequiredService<InboundConsumer>());
        builder.Services.AddSingleton<IHostedService>(services =>
            services.GetRequiredService<InboundConsumer>());
        if (builder.Configuration.GetValue<bool>("Telegram:RegistrationEnabled"))
        {
            builder.Services.AddHttpClient<INgrokTunnelClient, NgrokTunnelClient>(client =>
            {
                client.BaseAddress = new Uri("http://ngrok:4040/");
                client.Timeout = TimeSpan.FromSeconds(3);
            });
            builder.Services.AddHttpClient<IRegistrationClient, TelegramApiClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.telegram.org/");
                client.Timeout = TimeSpan.FromSeconds(10);
            }).RemoveAllLoggers(); // The bot token is part of the request path.
            builder.Services.AddHostedService<TelegramRegistration>();
        }
    }
}

try
{
    var app = builder.Build();

    if (!string.IsNullOrWhiteSpace(postgresConnection))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InboxDbContext>()
            .Database.MigrateAsync();
    }

    app.MapTelegramWebhook();
    app.MapReadiness();
    app.MapHub<InboxHub>("/hubs/inbox");
    if (!string.IsNullOrWhiteSpace(postgresConnection))
    {
        app.MapInboxQueries();
        app.MapInboxReplies();
        app.MapInboxReadState();
    }

    app.Run();
}
finally
{
    if (consumerConnection is not null)
        await consumerConnection.DisposeAsync();
}

public partial class Program { }
