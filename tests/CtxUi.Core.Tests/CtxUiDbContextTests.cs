using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class CtxUiDbContextTests
{
    [Fact]
    public async Task Migrations_create_schema_and_seed_revision_zero()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        Assert.Equal(0, (await db.RoutingStates.SingleAsync()).Revision);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Client_round_trip_keeps_utc_time_and_metadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        var expires = new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = database.NewContext())
        {
            var client = TestData.Client("Иванов 4419");
            client.ExpiresAt = expires;
            client.Metadata["storage-mb"] = "50";
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            var client = await db.Clients.SingleAsync();

            Assert.Equal("Иванов 4419", client.Name);
            Assert.Equal(expires, client.ExpiresAt);
            Assert.Equal(TimeSpan.Zero, client.ExpiresAt!.Value.Offset);
            Assert.Equal("50", client.Metadata["storage-mb"]);
            Assert.Null(client.LastUsedAt);
        }
    }

    [Fact]
    public async Task Metadata_edited_in_place_is_saved()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            db.Clients.Add(TestData.Client("Иванов"));
            await db.SaveChangesAsync();
            var client = await db.Clients.SingleAsync();
            client.Metadata["group"] = "4419";
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
            Assert.Equal("4419", (await db.Clients.SingleAsync()).Metadata["group"]);
    }

    [Fact]
    public async Task Tracked_save_bumps_revision_by_one()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        db.Inbounds.Add(TestData.Inbound("echo"));
        db.Clients.Add(TestData.Client("Иванов"));
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.RoutingStates.AsNoTracking().Select(s => s.Revision).SingleAsync());
    }

    [Fact]
    public async Task Save_without_changes_and_bulk_update_keep_revision()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        db.Clients.Add(TestData.Client("Иванов"));
        await db.SaveChangesAsync();

        await db.SaveChangesAsync();
        await db.Clients.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, TestData.Now));

        Assert.Equal(1, await db.RoutingStates.AsNoTracking().Select(s => s.Revision).SingleAsync());
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected_by_the_database()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        db.Inbounds.Add(TestData.Inbound("echo"));
        await db.SaveChangesAsync();

        db.Inbounds.Add(TestData.Inbound("echo"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_name_key_is_rejected_by_the_database()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        db.Clients.Add(TestData.Client("Иванов"));
        await db.SaveChangesAsync();

        var twin = TestData.Client("иванов");
        twin.TokenHash = AccessToken.Hash("another");
        db.Clients.Add(twin);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Removing_inbound_removes_access_links_but_keeps_client()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        var inbound = TestData.Inbound("echo");
        var client = TestData.Client("Иванов");
        client.Inbounds.Add(new ClientInbound { InboundId = inbound.Id });
        db.Inbounds.Add(inbound);
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        db.Inbounds.Remove(inbound);
        await db.SaveChangesAsync();

        Assert.Empty(await db.ClientInbounds.AsNoTracking().ToListAsync());
        Assert.Single(await db.Clients.AsNoTracking().ToListAsync());
    }
}
