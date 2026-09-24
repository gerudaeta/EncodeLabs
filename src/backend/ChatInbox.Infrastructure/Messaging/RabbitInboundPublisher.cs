using System.Text.Json;
using ChatInbox.Application.Inbound;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ChatInbox.Infrastructure.Messaging;

public sealed class UnroutablePublishException(bool returnObserved, bool brokerAckObserved)
    : IOException("Mandatory publication was returned after broker acknowledgement")
{
    public bool ReturnObserved { get; } = returnObserved;
    public bool BrokerAckObserved { get; } = brokerAckObserved;
}

public sealed class PublishNotConfirmedException(Exception cause)
    : IOException("Broker publication was not positively confirmed", cause);

public sealed class RabbitInboundPublisher : IInboundPublisher, IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _exchange;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private bool _unusable;

    private RabbitInboundPublisher(IConnection connection, IChannel channel, string exchange, TimeSpan timeout)
    {
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
        var factory = new ConnectionFactory
        {
            Uri = new Uri(amqpUri),
            AutomaticRecoveryEnabled = false
        };
        var connection = await factory.CreateConnectionAsync();
        try
        {
            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            var channel = await connection.CreateChannelAsync(options);
            return new RabbitInboundPublisher(connection, channel, exchange, timeout);
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
            if (_unusable || !_channel.IsOpen)
                throw new PublishNotConfirmedException(new IOException("Publisher channel is unavailable"));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);

            var sequence = await _channel.GetNextPublishSequenceNumberAsync(deadline.Token);
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

            _channel.BasicReturnAsync += OnReturn;
            _channel.BasicAcksAsync += OnAck;
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
                    await _channel.BasicPublishAsync(_exchange, RabbitTopology.InboundRoutingKey,
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
                _channel.BasicReturnAsync -= OnReturn;
                _channel.BasicAcksAsync -= OnAck;
            }
        }
        catch (UnroutablePublishException)
        {
            throw;
        }
        catch (PublishNotConfirmedException)
        {
            _unusable = true;
            throw;
        }
        catch (Exception error)
        {
            _unusable = true;
            throw new PublishNotConfirmedException(error);
        }
        finally
        {
            _publishGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
        _publishGate.Dispose();
    }
}
