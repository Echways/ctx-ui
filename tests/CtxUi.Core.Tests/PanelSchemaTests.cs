using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CtxUi.Core.Admins;
using CtxUi.Core.Data;
using CtxUi.Core.Settings;

namespace CtxUi.Core.Tests;

public class PanelSchemaTests
{
    private static Admin Admin(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        NameKey = name.ToUpperInvariant(),
        PasswordHash = "hash",
        SecurityStamp = "stamp",
        CreatedAt = TestData.Now,
    };

    private static Task<long> RevisionAsync(CtxUiDbContext db) =>
        db.RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    [Fact]
    public async Task Admin_round_trips_with_lockout_state()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            var admin = Admin("root");
            admin.FailedAttempts = 3;
            admin.LockedUntil = TestData.Now.AddMinutes(15);
            db.Admins.Add(admin);
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            var admin = await db.Admins.SingleAsync();

            Assert.Equal("root", admin.Name);
            Assert.Equal("ROOT", admin.NameKey);
            Assert.Equal("hash", admin.PasswordHash);
            Assert.Equal("stamp", admin.SecurityStamp);
            Assert.Equal(3, admin.FailedAttempts);
            Assert.Equal(TestData.Now.AddMinutes(15), admin.LockedUntil);
            Assert.Equal(TestData.Now, admin.CreatedAt);
        }
    }

    [Fact]
    public async Task Second_admin_with_the_same_name_key_is_rejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        db.Admins.Add(Admin("root"));
        await db.SaveChangesAsync();

        await using var other = database.NewContext();
        other.Admins.Add(Admin("Root"));

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task Setting_round_trips_and_its_key_is_unique()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            db.Settings.Add(new Setting { Key = "panel.path", Value = "a1b2c3" });
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
            Assert.Equal("a1b2c3", (await db.Settings.AsNoTracking().SingleAsync()).Value);

        await using (var db = database.NewContext())
        {
            db.Settings.Add(new Setting { Key = "panel.path", Value = "other" });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Admins_and_settings_do_not_bump_the_routing_revision()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        db.Admins.Add(Admin("root"));
        db.Settings.Add(new Setting { Key = "panel.path", Value = "a1b2c3" });
        await db.SaveChangesAsync();

        Assert.Equal(0, await RevisionAsync(db));
    }

    [Fact]
    public async Task Database_made_before_this_stage_gets_empty_tables_and_keeps_its_rows()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CtxUiDbContext>().UseSqlite(connection).Options;
        await using (var db = new CtxUiDbContext(options))
            await db.GetService<IMigrator>().MigrateAsync("Limits");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                INSERT INTO Inbounds (Id, Slug, Name, Enabled, Kind, RequestTimeoutSeconds, MaxBodyBytes, Url, ForwardIdentity, CreatedAt)
                VALUES ('22222222-2222-2222-2222-222222222222', 'echo', 'echo', 1, 0, 300, 4194304, 'http://upstream.test/mcp', 0, {TestData.Now.ToUnixTimeMilliseconds()});
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = new CtxUiDbContext(options))
        {
            await db.Database.MigrateAsync();

            Assert.Empty(await db.Admins.ToListAsync());
            Assert.Empty(await db.Settings.ToListAsync());
            Assert.Equal("echo", (await db.Inbounds.SingleAsync()).Slug);
            Assert.False(db.Database.HasPendingModelChanges());
        }
    }
}
