using ChatInbox.Domain;
using Xunit;

namespace ChatInbox.Tests;

public sealed class MessageReadStateTests
{
    [Fact]
    public void MarkReadTransitionsAnUnreadInboundMessage()
    {
        var message = new Message { Direction = MessageDirection.Inbound };
        var now = DateTimeOffset.Parse("2026-09-25T10:00:00Z");

        Assert.True(message.MarkRead(now));

        Assert.Equal(now, message.ReadAt);
    }

    [Fact]
    public void MarkReadIsIdempotentOnceAlreadyRead()
    {
        var readAt = DateTimeOffset.Parse("2026-09-25T09:00:00Z");
        var message = new Message { Direction = MessageDirection.Inbound, ReadAt = readAt };

        Assert.False(message.MarkRead(DateTimeOffset.Parse("2026-09-25T10:00:00Z")));

        Assert.Equal(readAt, message.ReadAt);
    }

    [Fact]
    public void OutboundMessagesAreNeverMarkedRead()
    {
        var message = new Message { Direction = MessageDirection.Outbound };

        Assert.False(message.MarkRead(DateTimeOffset.Parse("2026-09-25T10:00:00Z")));

        Assert.Null(message.ReadAt);
    }
}
