using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Tests;

public class UsageSchemaTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InboundId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static UsageEvent Event() => new()
    {
        ClientId = ClientId,
        InboundId = InboundId,
        At = TestData.Now,
        Method = "tools/call",
        Tool = "echo",
        DurationMs = 120,
        Outcome = UsageOutcome.UpstreamFailed,
    };

    private static UsageDaily Daily() => new()
    {
        Day = DateOnly.FromDateTime(TestData.Now.UtcDateTime),
        ClientId = ClientId,
        InboundId = InboundId,
        Calls = 3,
        ToolCalls = 2,
        Errors = 1,
        Refused = 4,
        DurationMs = 5_000_000_000,
    };

    private static Task<long> RevisionAsync(CtxUiDbContext db) =>
        db.RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    [Fact]
    public async Task Rows_written_before_this_stage_get_working_defaults()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CtxUiDbContext>().UseSqlite(connection).Options;
        var created = TestData.Now.ToUnixTimeMilliseconds();
        await using (var db = new CtxUiDbContext(options))
            await db.GetService<IMigrator>().MigrateAsync("Initial");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                INSERT INTO Inbounds (Id, Slug, Name, Enabled, Kind, RequestTimeoutSeconds, Url, ForwardIdentity, CreatedAt)
                VALUES ('{InboundId.ToString().ToUpperInvariant()}', 'echo', 'echo', 1, 0, 300, 'http://upstream.test/mcp', 0, {created});
                INSERT INTO Clients (Id, Name, NameKey, Enabled, TokenHash, TokenSealed, TokenPrefix, CreatedAt, Metadata)
                VALUES ('{ClientId.ToString().ToUpperInvariant()}', 'Иванов', 'ИВАНОВ', 1, 'hash', 'sealed', 'tnk_test00', {created}, '{"{}"}');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = new CtxUiDbContext(options))
        {
            await db.Database.MigrateAsync();
            var inbound = await db.Inbounds.SingleAsync();
            var client = await db.Clients.SingleAsync();

            Assert.Equal(Inbound.DefaultMaxBodyBytes, inbound.MaxBodyBytes);
            Assert.Equal(TestData.Now, client.PeriodStartedAt);
            Assert.Null(client.RatePerMinute);
            Assert.Null(client.MaxConcurrency);
            Assert.Null(client.QuotaCalls);
            Assert.Null(client.ResetEveryDays);
            Assert.Equal(0, client.UsedCalls);
            Assert.Null(client.UsedSince);
        }
    }

    [Fact]
    public async Task Client_limits_and_counters_round_trip()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            var client = TestData.Client("Иванов");
            client.RatePerMinute = 60;
            client.MaxConcurrency = 2;
            client.QuotaCalls = 100;
            client.ResetEveryDays = 30;
            client.PeriodStartedAt = TestData.Now.AddDays(-3);
            client.UsedCalls = 5_000_000_000;
            client.UsedSince = TestData.Now.AddDays(-3);
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            var client = await db.Clients.SingleAsync();

            Assert.Equal(60, client.RatePerMinute);
            Assert.Equal(2, client.MaxConcurrency);
            Assert.Equal(100, client.QuotaCalls);
            Assert.Equal(30, client.ResetEveryDays);
            Assert.Equal(TestData.Now.AddDays(-3), client.PeriodStartedAt);
            Assert.Equal(5_000_000_000, client.UsedCalls);
            Assert.Equal(TestData.Now.AddDays(-3), client.UsedSince);
        }
    }

    [Fact]
    public async Task Usage_rows_round_trip()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            db.UsageEvents.Add(Event());
            db.UsageDays.Add(Daily());
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            var usage = await db.UsageEvents.SingleAsync();
            var daily = await db.UsageDays.SingleAsync();

            Assert.True(usage.Id > 0);
            Assert.Equal(TestData.Now, usage.At);
            Assert.Equal("echo", usage.Tool);
            Assert.Equal(UsageOutcome.UpstreamFailed, usage.Outcome);
            Assert.Equal(new DateOnly(2026, 10, 12), daily.Day);
            Assert.Equal(5_000_000_000, daily.DurationMs);
            Assert.Equal(4, daily.Refused);
        }
    }

    [Fact]
    public async Task Second_daily_row_for_the_same_day_client_and_inbound_is_rejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        db.UsageDays.Add(Daily());
        await db.SaveChangesAsync();

        await using var other = database.NewContext();
        other.UsageDays.Add(Daily());

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task Usage_rows_alone_do_not_bump_the_routing_revision()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        db.UsageEvents.Add(Event());
        db.UsageDays.Add(Daily());
        await db.SaveChangesAsync();

        Assert.Equal(0, await RevisionAsync(db));
    }

    [Fact]
    public async Task Routing_change_saved_together_with_usage_rows_bumps_the_revision_once()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        db.UsageEvents.Add(Event());
        db.Clients.Add(TestData.Client("Иванов"));
        await db.SaveChangesAsync();

        Assert.Equal(1, await RevisionAsync(db));
    }
}
