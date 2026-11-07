using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CtxUi.Core.Data;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CtxUiDbContext>
{
    public CtxUiDbContext CreateDbContext(string[] args)
    {
        return new CtxUiDbContext(new DbContextOptionsBuilder<CtxUiDbContext>().UseSqlite("Data Source=design-time.db")
            .Options);
    }
}
