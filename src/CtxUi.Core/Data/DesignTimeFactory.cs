using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CtxUi.Core.Data;

/// <summary>Нужна только команде dotnet ef, чтобы построить модель без запуска сервера.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CtxUiDbContext>
{
    public CtxUiDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CtxUiDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
