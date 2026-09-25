using ChatInbox.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatInbox.Infrastructure.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("messages");
        b.HasKey(x => x.Id).HasName("pk_messages");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ConversationId).HasColumnName("conversation_id");
        b.Property(x => x.TelegramMessageId).HasColumnName("telegram_message_id");
        b.Property(x => x.TelegramUpdateId).HasColumnName("telegram_update_id");
        b.Property(x => x.Text).HasColumnName("text").IsRequired();
        b.Property(x => x.SentAt).HasColumnName("sent_at").IsRequired();
        b.Property(x => x.Direction).HasColumnName("direction").IsRequired()
            .HasConversion(d => d.ToStorageValue(), s => MessageDirectionCodec.FromStorageValue(s));
        b.Property(x => x.ReadAt).HasColumnName("read_at");
        b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId);
        b.HasIndex(x => x.TelegramUpdateId).IsUnique().HasDatabaseName("ux_messages_update");
        b.HasIndex(x => new { x.ConversationId, x.TelegramMessageId }).IsUnique()
            .HasDatabaseName("ux_messages_chat_message");
        b.HasIndex(x => new { x.ConversationId, x.SentAt, x.Id })
            .HasDatabaseName("ix_messages_conversation_time");
        // Speeds up per-conversation unread counts without scanning read/outbound rows.
        b.HasIndex(x => x.ConversationId).HasDatabaseName("ix_messages_unread")
            .HasFilter($"direction = '{MessageDirectionCodec.InboundValue}' AND read_at IS NULL");
    }
}
