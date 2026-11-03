using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CtxUi.Builder.Data;

public sealed class BuilderDatabaseFactory : IDesignTimeDbContextFactory<BuilderDatabase>
{
    public BuilderDatabase CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BuilderDatabase>().UseSqlite("Data Source=design.db").Options);
}
