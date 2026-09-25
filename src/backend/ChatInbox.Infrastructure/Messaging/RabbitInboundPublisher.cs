using System.Text.Json;
using ChatInbox.Application.Inbound;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ChatInbox.Infrastructure.Messaging;

public sealed class UnroutablePublishException(bool returnObserved, bool brokerAckObserved)
    : IOException("Mandatory publication was returned after broker acknowledgement")
{
    public bool ReturnObserved { get; } = returnObserved;
    public bool BrokerAckObserved { get; } = brokerAckObserved;
}

public sealed class RabbitPublisherHostedService(string amqpUri, TimeSpan timeout)
    : IHostedService, IInboundPublisher
{
    private RabbitInboundPublisher? _publisher;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(amqpUri),
            AutomaticRecoveryEnabled = false
        };
        await using (var connection = await factory.CreateConnectionAsync(cancellationToken))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken))
        {
            await RabbitTopology.DeclareAsync(channel, cancellationToken);
        }
        _publisher = await RabbitInboundPublisher.ConnectAsync(
            amqpUri, RabbitTopology.InboundExchange, timeout);
    }

    public Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken)
    {
        var publisher = _publisher;
        if (publisher is null)
            throw new PublishNotConfirmedException(new IOException("Publisher is not running"));
        return publisher.PublishConfirmedAsync(update, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var publisher = Interlocked.Exchange(ref _publisher, null);
        if (publisher is not null)
            await publisher.DisposeAsync();
    }
}

public sealed class PublishNotConfirmedException(Exception cause)
    : IOException("Broker publication was not positively confirmed", cause);

public sealed class RabbitInboundPublisher : IInboundPublisher, IAsyncDisposable
{
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly string _amqpUri;
    private readonly string _exchange;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private bool _disposed;

    private RabbitInboundPublisher(
        string amqpUri, IConnection connection, IChannel channel, string exchange, TimeSpan timeout)
    {
        _amqpUri = amqpUri;
        _connection = connection;
        _channel = channel;
        _exchange = exchange;
        _timeout = timeout;
    }

    public static async Task<RabbitInboundPublisher> ConnectAsync(
        string amqpUri, string exchange, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(timeout);
        var (connection, channel) = await OpenAsync(amqpUri, deadline.Token);
        return new RabbitInboundPublisher(amqpUri, connection, channel, exchange, timeout);
    }

    private static async Task<(IConnection Connection, IChannel Channel)> OpenAsync(
        string amqpUri, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(amqpUri),
            AutomaticRecoveryEnabled = false
        };
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        try
        {
            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            var channel = await connection.CreateChannelAsync(options, cancellationToken);
            return (connection, channel);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            await _publishGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException error)
        {
            throw new PublishNotConfirmedException(error);
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            if (_channel is null || !_channel.IsOpen)
            {
                await RetireAsync();
                (_connection, _channel) = await OpenAsync(_amqpUri, deadline.Token);
            }
            var channel = _channel;

            var sequence = await channel.GetNextPublishSequenceNumberAsync(deadline.Token);
            var returned = false;
            var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnReturn(object sender, BasicReturnEventArgs args)
            {
                returned = true;
                return Task.CompletedTask;
            }

            Task OnAck(object sender, BasicAckEventArgs args)
            {
                if (args.DeliveryTag == sequence || (args.Multiple && args.DeliveryTag > sequence))
                    acked.TrySetResult();
                return Task.CompletedTask;
            }

            channel.BasicReturnAsync += OnReturn;
            channel.BasicAcksAsync += OnAck;
            try
            {
                var properties = new BasicProperties
                {
                    ContentType = "application/json",
                    Persistent = true,
                    Headers = new Dictionary<string, object?> { ["schema-version"] = update.Version }
                };
                Exception? publishError = null;
                try
                {
                    await channel.BasicPublishAsync(_exchange, RabbitTopology.InboundRoutingKey,
                        mandatory: true, basicProperties: properties,
                        body: JsonSerializer.SerializeToUtf8Bytes(update), cancellationToken: deadline.Token);
                }
                catch (Exception error)
                {
                    publishError = error;
                }

                if (returned)
                {
                    // Client-side confirm tracking throws on basic.return before broker basic.ack.
                    await acked.Task.WaitAsync(deadline.Token);
                    throw new UnroutablePublishException(returnObserved: true, brokerAckObserved: true);
                }
                if (publishError is not null)
                    throw new PublishNotConfirmedException(publishError);

                await acked.Task.WaitAsync(deadline.Token);
                if (returned)
                    throw new UnroutablePublishException(returnObserved: true, brokerAckObserved: true);
            }
            finally
            {
                channel.BasicReturnAsync -= OnReturn;
                channel.BasicAcksAsync -= OnAck;
            }
        }
        catch (UnroutablePublishException)
        {
            throw;
        }
        catch (PublishNotConfirmedException)
        {
            await RetireAsync();
            throw;
        }
        catch (Exception error)
        {
            await RetireAsync();
            throw new PublishNotConfirmedException(error);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _publishGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await RetireAsync();
        }
        finally
        {
            _publishGate.Release();
        }
        _publishGate.Dispose();
    }

    private async Task RetireAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try
        {
            if (channel is not null) await channel.DisposeAsync();
        }
        catch (Exception)
        {
            // Preserve the publication outcome; this channel will never be reused.
        }
        try
        {
            if (connection is not null) await connection.DisposeAsync();
        }
        catch (Exception)
        {
            // Preserve the publication outcome; a later request opens a new connection.
        }
    }
}
