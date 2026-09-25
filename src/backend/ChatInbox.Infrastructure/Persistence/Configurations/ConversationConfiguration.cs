using ChatInbox.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatInbox.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("conversations");
        b.HasKey(x => x.Id).HasName("pk_conversations");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TelegramChatId).HasColumnName("telegram_chat_id");
        b.Property(x => x.DisplayName).HasColumnName("display_name");
        b.Property(x => x.LastMessageAt).HasColumnName("last_message_at");
        b.Property(x => x.LastTelegramMessageId).HasColumnName("last_telegram_message_id");
        b.Property(x => x.LastMessagePreview).HasColumnName("last_message_preview");
        b.HasIndex(x => x.TelegramChatId).IsUnique().HasDatabaseName("ux_conversations_chat");
        b.HasIndex(x => new { x.LastMessageAt, x.Id }).IsDescending()
            .HasDatabaseName("ix_conversations_activity");
    }
}
