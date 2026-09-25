using ChatInbox.Domain;
using ChatInbox.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace ChatInbox.Infrastructure.Persistence;

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
        modelBuilder.ApplyConfiguration(new ConversationConfiguration());
        modelBuilder.ApplyConfiguration(new MessageConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedUpdateConfiguration());
    }
}
