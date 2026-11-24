using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MockServer.Infrastructure;

namespace MockServer.Api;

public sealed class DesignTimeDatabase : IDesignTimeDbContextFactory<MockDbContext>
{
    public MockDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MockDbContext>()
        .UseSqlite("Data Source=data/mock.db").Options);
}
