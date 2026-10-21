using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Tests;

public sealed class DatabaseSetupTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ctx-ui-db-");

    private string File => Path.Combine(_directory.FullName, "ctx-ui.db");

    private CtxUiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CtxUiDbContext>().UseSqlite($"Data Source={File}").Options);

    private string JournalMode(string pragma = "PRAGMA journal_mode")
    {
        using var connection = new SqliteConnection($"Data Source={File}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        return (string)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }

    [Fact]
    public async Task Prepared_database_runs_in_wal_mode()
    {
        await using (var db = NewContext())
            await db.PrepareAsync();

        Assert.Equal("wal", JournalMode());
    }

    [Fact]
    public async Task Existing_database_in_rollback_mode_switches_and_keeps_its_data()
    {
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();
            await new InboundService(db, new FakeVault(), new FakeTimeProvider(TestData.Now))
                .AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        }

        var before = JournalMode("PRAGMA journal_mode=DELETE");
        await using (var db = NewContext())
            await db.PrepareAsync();

        await using var check = NewContext();
        Assert.Equal("delete", before);
        Assert.Equal("wal", JournalMode());
        Assert.Equal("echo", Assert.Single(await check.Inbounds.ToListAsync()).Slug);
    }

    [Fact]
    public async Task Preparing_twice_changes_nothing()
    {
        await using (var db = NewContext())
            await db.PrepareAsync();
        await using (var db = NewContext())
            await db.PrepareAsync();

        await using var check = NewContext();
        Assert.Equal("wal", JournalMode());
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Database_in_memory_is_prepared_without_errors()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        await db.PrepareAsync();

        Assert.Empty(await db.Inbounds.ToListAsync());
    }
}
