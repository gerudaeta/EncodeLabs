using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChatInbox.Infrastructure.Persistence;

public sealed class InboxDbContextFactory : IDesignTimeDbContextFactory<InboxDbContext>
{
    public InboxDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<InboxDbContext>()
            .UseNpgsql("Host=localhost;Database=inbox_design_time")
            .Options);
}
