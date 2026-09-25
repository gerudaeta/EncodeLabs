using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatInbox.Infrastructure.Persistence.Configurations;

internal sealed class ProcessedUpdateConfiguration : IEntityTypeConfiguration<ProcessedUpdate>
{
    public void Configure(EntityTypeBuilder<ProcessedUpdate> b)
    {
        b.ToTable("processed_updates");
        b.HasKey(x => x.UpdateId).HasName("pk_processed_updates");
        b.Property(x => x.UpdateId).HasColumnName("update_id").ValueGeneratedNever();
        b.Property(x => x.ProcessedAt).HasColumnName("processed_at").IsRequired();
    }
}
