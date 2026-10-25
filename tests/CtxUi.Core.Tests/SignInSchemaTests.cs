using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class SignInSchemaTests
{
    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private static Device Device(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        MarkerHash = "marker",
        Name = "Chrome · Windows",
        SignInIp = "203.0.113.7",
        SignedInWith = SignIn.Device.ByKey,
        CreatedAt = TestData.Now,
    };

    private static ClientLink Link(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Provider = "google",
        Subject = "user-1",
        Display = "ivanov@example.org",
        LinkedAt = TestData.Now,
    };

    private static Task<long> RevisionAsync(CtxUiDbContext db) =>
        db.RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    private static async Task<Client> SavedClientAsync(TestDatabase database, string name)
    {
        await using var db = database.NewContext();
        var client = TestData.Client(name);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    [Fact]
    public async Task Fresh_database_has_oauth_off_with_key_sign_in_ready()
    {
        await using var database = await TestDatabase.CreateAsync();

        var settings = await database.NewContext().SignInSettings.SingleAsync();

        Assert.False(settings.Enabled);
        Assert.True(settings.KeySignIn);
        Assert.True(settings.DefaultDirectKey);
        Assert.Null(settings.DefaultMaxDevices);
        Assert.Equal(30, settings.IdleDays);
        Assert.True(settings.SelfRevoke);
    }

    [Fact]
    public async Task Existing_clients_keep_the_direct_key_and_no_device_limit()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");

        var issued = await new ClientService(database.NewContext(), _vault, _clock).AddAsync("Иванов", ["echo"]);

        var client = await database.NewContext().Clients.AsNoTracking().SingleAsync(saved => saved.Id == issued.Client.Id);
        Assert.True(client.DirectKey);
        Assert.Null(client.MaxDevices);
        Assert.Null(client.InviteHash);
        Assert.Null(client.InviteExpiresAt);
    }

    [Fact]
    public async Task One_external_account_links_to_one_client_only()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ivanov = await SavedClientAsync(database, "Иванов");
        var petrov = await SavedClientAsync(database, "Петров");
        await using (var db = database.NewContext())
        {
            db.ClientLinks.Add(Link(ivanov.Id));
            await db.SaveChangesAsync();
        }

        await using var other = database.NewContext();
        other.ClientLinks.Add(Link(petrov.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task Removing_a_client_removes_its_devices_and_links()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = await SavedClientAsync(database, "Иванов");
        await using (var db = database.NewContext())
        {
            var device = Device(client.Id);
            device.Apps.Add(new DeviceApp
            {
                DeviceId = device.Id,
                AppKey = "https://claude.ai/oauth/mcp-oauth-client-metadata",
                AppName = "Claude",
                AuthorizationId = "authorization",
                ConnectedAt = TestData.Now,
            });
            db.Devices.Add(device);
            db.ClientLinks.Add(Link(client.Id));
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            db.Clients.Remove(await db.Clients.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using var check = database.NewContext();
        Assert.Empty(await check.Devices.ToListAsync());
        Assert.Empty(await check.DeviceApps.ToListAsync());
        Assert.Empty(await check.ClientLinks.ToListAsync());
    }

    [Fact]
    public async Task Device_changes_bump_the_routing_revision_but_app_rows_do_not()
    {
        await using var database = await TestDatabase.CreateAsync();
        var client = await SavedClientAsync(database, "Иванов");
        var device = Device(client.Id);
        await using (var db = database.NewContext())
        {
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            Assert.Equal(2, await RevisionAsync(db));
        }

        await using (var db = database.NewContext())
        {
            db.DeviceApps.Add(new DeviceApp
            {
                DeviceId = device.Id,
                AppKey = "dcr:abc",
                AppName = "Cursor",
                AuthorizationId = "authorization",
                ConnectedAt = TestData.Now,
            });
            await db.SaveChangesAsync();
            Assert.Equal(2, await RevisionAsync(db));
        }

        await using (var db = database.NewContext())
        {
            var settings = await db.SignInSettings.SingleAsync();
            settings.Enabled = true;
            db.SignInProviders.Add(new SignInProvider { Name = "google", ClientId = "id", SecretSealed = "sealed" });
            db.ClientLinks.Add(Link(client.Id));
            await db.SaveChangesAsync();
            Assert.Equal(3, await RevisionAsync(db));
        }
    }

    [Fact]
    public async Task Openiddict_tables_exist()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();

        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'").ToListAsync();

        Assert.Contains("OpenIddictApplications", tables);
        Assert.Contains("OpenIddictAuthorizations", tables);
        Assert.Contains("OpenIddictScopes", tables);
        Assert.Contains("OpenIddictTokens", tables);
    }
}
