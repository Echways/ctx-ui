using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleExtension.Data;

public sealed class SampleNote
{
    public int Id { get; set; }

    public required string Text { get; set; }
}

public sealed class SampleDatabase(DbContextOptions<SampleDatabase> options) : DbContext(options)
{
    public DbSet<SampleNote> Notes => Set<SampleNote>();
}

public sealed class SampleDatabaseFactory : IDesignTimeDbContextFactory<SampleDatabase>
{
    public SampleDatabase CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SampleDatabase>().UseSqlite("Data Source=design.db").Options);
}
