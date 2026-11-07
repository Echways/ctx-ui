using CtxUi.Builder.Data;
using CtxUi.Builder.Servers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CtxUi.Builder.Tests;

public sealed class CatalogHarness : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly List<BuilderDatabase> _databases = [];
    private DbContextOptions<BuilderDatabase> _options = null!;

    public SecretBox Box { get; } = new(new EphemeralDataProtectionProvider());

    public ServerCatalog Catalog { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _options = new DbContextOptionsBuilder<BuilderDatabase>()
            .UseSqlite($"Data Source={Path.Combine(_directory, "data.db")}")
            .Options;
        await Open().Database.MigrateAsync();
        Catalog = AnotherCatalog();
    }

    public ServerCatalog AnotherCatalog()
    {
        return new ServerCatalog(new PooledDbContextFactory<BuilderDatabase>(_options), Box, TimeProvider.System);
    }

    public BuilderDatabase Inspect()
    {
        return Open();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
            database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
        return ValueTask.CompletedTask;
    }

    private BuilderDatabase Open()
    {
        var database = new BuilderDatabase(_options);
        _databases.Add(database);
        return database;
    }
}
