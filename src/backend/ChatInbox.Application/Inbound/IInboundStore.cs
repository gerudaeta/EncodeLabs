namespace ChatInbox.Application.Inbound;

public enum StoreOutcome
{
    Inserted,
    AlreadyProcessed
}

public interface IInboundStore
{
    Task<StoreOutcome> StoreAsync(InboundTelegramUpdate update, CancellationToken cancellationToken);
}
