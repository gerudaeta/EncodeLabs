using Microsoft.EntityFrameworkCore;

namespace ChatInbox.Infrastructure.Persistence;

public sealed class Conversation
{
    public Guid Id { get; set; }
    public long TelegramChatId { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public long? LastTelegramMessageId { get; set; }
    public string? LastMessagePreview { get; set; }
}

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public long TelegramMessageId { get; set; }
    public long? TelegramUpdateId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public string Direction { get; set; } = "inbound";
}

public sealed class ProcessedUpdate
{
    public long UpdateId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class InboxDbContext(DbContextOptions<InboxDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ProcessedUpdate> ProcessedUpdates => Set<ProcessedUpdate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(b =>
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
        });

        modelBuilder.Entity<Message>(b =>
        {
            b.ToTable("messages");
            b.HasKey(x => x.Id).HasName("pk_messages");
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.ConversationId).HasColumnName("conversation_id");
            b.Property(x => x.TelegramMessageId).HasColumnName("telegram_message_id");
            b.Property(x => x.TelegramUpdateId).HasColumnName("telegram_update_id");
            b.Property(x => x.Text).HasColumnName("text").IsRequired();
            b.Property(x => x.SentAt).HasColumnName("sent_at").IsRequired();
            b.Property(x => x.Direction).HasColumnName("direction").IsRequired();
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId);
            b.HasIndex(x => x.TelegramUpdateId).IsUnique().HasDatabaseName("ux_messages_update");
            b.HasIndex(x => new { x.ConversationId, x.TelegramMessageId }).IsUnique()
                .HasDatabaseName("ux_messages_chat_message");
            b.HasIndex(x => new { x.ConversationId, x.SentAt, x.Id })
                .HasDatabaseName("ix_messages_conversation_time");
        });

        modelBuilder.Entity<ProcessedUpdate>(b =>
        {
            b.ToTable("processed_updates");
            b.HasKey(x => x.UpdateId).HasName("pk_processed_updates");
            b.Property(x => x.UpdateId).HasColumnName("update_id").ValueGeneratedNever();
            b.Property(x => x.ProcessedAt).HasColumnName("processed_at").IsRequired();
        });
    }
}
