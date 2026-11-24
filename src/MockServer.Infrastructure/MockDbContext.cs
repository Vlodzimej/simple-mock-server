using Microsoft.EntityFrameworkCore;

namespace MockServer.Infrastructure;

public sealed class MockDbContext(DbContextOptions<MockDbContext> options) : DbContext(options)
{
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<SessionRequest> SessionRequests => Set<SessionRequest>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SessionRequest>().HasIndex(row => new { row.SessionId, row.Id });
        model.Entity<SessionRequest>().HasOne<SessionRow>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SessionRow
{
    public string Id { get; set; } = "";
    public string Payload { get; set; } = "";
}

public sealed class SessionRequest
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? RuleId { get; set; }
    public string Source { get; set; } = "";
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string Reason { get; set; } = "";
}
