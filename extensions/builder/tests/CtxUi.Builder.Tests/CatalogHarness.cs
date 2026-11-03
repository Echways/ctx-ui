using CtxUi.Builder.Data;
using CtxUi.Builder.Servers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Tests;

public sealed class CatalogHarness : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly List<BuilderDatabase> _databases = [];

    public SecretBox Box { get; } = new(new EphemeralDataProtectionProvider());

    public ServerCatalog Catalog { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = Open();
        await database.Database.MigrateAsync();
        Catalog = new ServerCatalog(database, Box, TimeProvider.System);
    }

    public ServerCatalog AnotherCatalog() => new(Open(), Box, TimeProvider.System);

    public BuilderDatabase Inspect() => Open();

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
        var options = new DbContextOptionsBuilder<BuilderDatabase>()
            .UseSqlite($"Data Source={Path.Combine(_directory, "data.db")}")
            .Options;
        var database = new BuilderDatabase(options);
        _databases.Add(database);
        return database;
    }
}
