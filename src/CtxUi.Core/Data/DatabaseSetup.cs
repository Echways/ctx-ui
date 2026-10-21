using Microsoft.EntityFrameworkCore;

namespace CtxUi.Core.Data;

public static class DatabaseSetup
{
    public static async Task PrepareAsync(this CtxUiDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", ct);
    }
}
