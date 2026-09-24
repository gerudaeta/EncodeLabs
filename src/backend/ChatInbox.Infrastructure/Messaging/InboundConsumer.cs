using System.Diagnostics.Metrics;
using System.Text.Json;
using ChatInbox.Application.Inbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ChatInbox.Infrastructure.Messaging;

public interface IInboundConsumerReadiness
{
    bool IsReady { get; }
    Task WaitReadyAsync(CancellationToken cancellationToken);
}

internal sealed class ConsumerSubscriptionReadiness : IInboundConsumerReadiness
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _subscribed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ready;
    private bool _terminal;

    public bool IsReady { get { lock (_gate) return _ready; } }

    public async Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        await _subscribed.Task.WaitAsync(cancellationToken);
        if (!IsReady) throw new IOException("Inbound consumer is not subscribed.");
    }

    public bool TryMarkSubscribed()
    {
        lock (_gate)
        {
            if (_terminal) return false;
            _ready = true;
            _subscribed.TrySetResult();
            return true;
        }
    }

    public void MarkTerminal()
    {
        lock (_gate)
        {
            _terminal = true;
            _ready = false;
            _subscribed.TrySetException(new IOException("Inbound consumer stopped before subscription."));
        }
    }
}

public sealed class InboundConsumer(
    IConnection connection,
    IServiceScopeFactory scopes,
    ILogger<InboundConsumer> logger) : BackgroundService, IInboundConsumerReadiness
{
    private readonly ConsumerSubscriptionReadiness _readiness = new();
    public bool IsReady => _readiness.IsReady;
    public Task WaitReadyAsync(CancellationToken cancellationToken) =>
        _readiness.WaitReadyAsync(cancellationToken);

    private static readonly Meter Meter = new("ChatInbox.Inbound");
    private static readonly Counter<long> ProcessingFailures =
        Meter.CreateCounter<long>("chatinbox.inbound.processing_failures");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunAsync(stoppingToken); }
        catch { _readiness.MarkTerminal(); throw; }
        finally { _readiness.MarkTerminal(); }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false,
            cancellationToken: stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        var terminal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnCancelled(object _, ConsumerEventArgs __)
        {
            _readiness.MarkTerminal();
            if (!stoppingToken.IsCancellationRequested) terminal.TrySetResult("consumer_cancelled");
            return Task.CompletedTask;
        }
        Task OnChannelShutdown(object _, ShutdownEventArgs __)
        {
            _readiness.MarkTerminal();
            if (!stoppingToken.IsCancellationRequested) terminal.TrySetResult("channel_shutdown");
            return Task.CompletedTask;
        }
        Task OnConnectionShutdown(object _, ShutdownEventArgs __)
        {
            _readiness.MarkTerminal();
            if (!stoppingToken.IsCancellationRequested) terminal.TrySetResult("connection_shutdown");
            return Task.CompletedTask;
        }
        Task OnCallbackException(object _, CallbackExceptionEventArgs __)
        {
            _readiness.MarkTerminal();
            if (!stoppingToken.IsCancellationRequested) terminal.TrySetResult("consumer_callback_failure");
            return Task.CompletedTask;
        }

        consumer.UnregisteredAsync += OnCancelled;
        channel.ChannelShutdownAsync += OnChannelShutdown;
        channel.CallbackExceptionAsync += OnCallbackException;
        connection.ConnectionShutdownAsync += OnConnectionShutdown;
        consumer.ReceivedAsync += async (_, delivery) =>
            await ProcessAsync(channel, delivery, stoppingToken);
        try
        {
            await channel.BasicConsumeAsync(RabbitTopology.InboundQueue,
                autoAck: false, consumer, cancellationToken: stoppingToken);
            if (!channel.IsOpen || !_readiness.TryMarkSubscribed())
                throw new IOException("Inbound consumer stopped during subscription.");
            var category = await terminal.Task.WaitAsync(stoppingToken);
            logger.LogError("Inbound consumer stopped: {Category}", category);
            throw new IOException($"Inbound consumer stopped: {category}");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Closing the channel requeues any delivery still awaiting an acknowledgement.
        }
        finally
        {
            connection.ConnectionShutdownAsync -= OnConnectionShutdown;
            channel.CallbackExceptionAsync -= OnCallbackException;
            channel.ChannelShutdownAsync -= OnChannelShutdown;
            consumer.UnregisteredAsync -= OnCancelled;
        }
    }

    private async Task ProcessAsync(IChannel channel, BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        InboundTelegramUpdate update;
        try
        {
            update = JsonSerializer.Deserialize<InboundTelegramUpdate>(delivery.Body.Span)
                ?? throw new JsonException("Missing envelope");
            if (update.Version != 1 || update.UpdateId == 0 || update.ChatId == 0 ||
                update.MessageId == 0 || update.SentAt == default || string.IsNullOrWhiteSpace(update.Text))
                throw new JsonException("Unsupported or incomplete envelope");
        }
        catch (JsonException)
        {
            ProcessingFailures.Add(1, new KeyValuePair<string, object?>("category", "invalid_envelope"));
            logger.LogWarning("Rejected inbound envelope: {Category}", "invalid_envelope");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false,
                cancellationToken: stoppingToken);
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IInboundStore>()
                .StoreAsync(update, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // The channel closes on shutdown; RabbitMQ will redeliver.
        }
        catch (Exception)
        {
            ProcessingFailures.Add(1, new KeyValuePair<string, object?>("category", "store_failure"));
            logger.LogWarning("Rejected inbound update {UpdateId}: {Category}",
                update.UpdateId, "store_failure");
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true,
                cancellationToken: stoppingToken);
            return;
        }

        // Both Inserted and AlreadyProcessed return only after a committed database outcome.
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false,
            cancellationToken: stoppingToken);
    }
}
