namespace ChatInbox.Domain;

public enum MessageDirection
{
    Inbound,
    Outbound
}

/// <summary>
/// Maps <see cref="MessageDirection"/> to/from the exact strings persisted in the
/// database ("inbound"/"outbound"), so the mapping change is schema-invisible.
/// </summary>
public static class MessageDirectionCodec
{
    public const string InboundValue = "inbound";
    public const string OutboundValue = "outbound";

    public static string ToStorageValue(this MessageDirection direction) => direction switch
    {
        MessageDirection.Inbound => InboundValue,
        MessageDirection.Outbound => OutboundValue,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown message direction")
    };

    public static MessageDirection FromStorageValue(string value) => value switch
    {
        InboundValue => MessageDirection.Inbound,
        OutboundValue => MessageDirection.Outbound,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown message direction")
    };
}
