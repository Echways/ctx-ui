using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;

namespace CtxUi.Core.Tests;

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<CtxUiDbContext> _options;

    private TestDatabase() =>
        _options = new DbContextOptionsBuilder<CtxUiDbContext>().UseSqlite(_connection).Options;

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await database._connection.OpenAsync();
        await using var db = database.NewContext();
        await db.PrepareAsync();
        return database;
    }

    public CtxUiDbContext NewContext() => new(_options);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
