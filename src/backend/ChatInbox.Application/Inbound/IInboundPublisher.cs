namespace ChatInbox.Application.Inbound;

public interface IInboundPublisher
{
    Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken);
}
