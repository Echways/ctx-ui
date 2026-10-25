using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class ClientAccessTests
{
    private const string PublicUrl = "https://mcp.example.org";

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private SignInSettingsService Settings(TestDatabase database) => new(database.NewContext(), _vault);

    private async Task<TestDatabase> DatabaseWithInboundsAsync(params string[] slugs)
    {
        var database = await TestDatabase.CreateAsync();
        foreach (var slug in slugs)
            await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync(slug, null, "https://mcp.example.org/mcp");

        return database;
    }

    private static async Task AddDeviceAsync(TestDatabase database, Guid clientId, string signedInWith, DateTimeOffset? revokedAt = null)
    {
        await using var db = database.NewContext();
        db.Devices.Add(new Device
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            MarkerHash = Guid.NewGuid().ToString(),
            Name = "Chrome · Windows",
            SignInIp = "203.0.113.7",
            SignedInWith = signedInWith,
            CreatedAt = TestData.Now,
            RevokedAt = revokedAt,
            RevokedReason = revokedAt is null ? null : DeviceRevocation.Admin,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task New_client_takes_device_limit_and_direct_key_from_the_defaults()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Settings(database).UpdateAsync(
            new SignInSettingsUpdate { ChangeDefaultMaxDevices = true, DefaultMaxDevices = 2, DefaultDirectKey = false },
            PublicUrl);

        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        Assert.Equal(2, issued.Client.MaxDevices);
        Assert.False(issued.Client.DirectKey);
        var saved = await Clients(database).GetAsync(issued.Client.Id);
        Assert.Equal(2, saved.MaxDevices);
        Assert.False(saved.DirectKey);
    }

    [Fact]
    public async Task Given_access_policy_overrides_the_defaults()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Settings(database).UpdateAsync(
            new SignInSettingsUpdate { ChangeDefaultMaxDevices = true, DefaultMaxDevices = 2, DefaultDirectKey = false },
            PublicUrl);

        var issued = await Clients(database).AddAsync("Иванов", ["echo"], access: new ClientAccessPolicy(null, true));

        var saved = await Clients(database).GetAsync(issued.Client.Id);
        Assert.Null(saved.MaxDevices);
        Assert.True(saved.DirectKey);
    }

    [Fact]
    public async Task Update_replaces_the_access_policy_as_a_whole()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Access = new ClientAccessPolicy(3, false) });
        var limited = await Clients(database).GetAsync(issued.Client.Id);
        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Access = new ClientAccessPolicy(null, true) });
        var open = await Clients(database).GetAsync(issued.Client.Id);

        Assert.Equal((3, false), (limited.MaxDevices, limited.DirectKey));
        Assert.Equal((null, true), (open.MaxDevices, open.DirectKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Device_limit_is_between_1_and_1000(int limit)
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        await Assert.ThrowsAsync<DomainException>(() =>
            Clients(database).AddAsync("Петров", ["echo"], access: new ClientAccessPolicy(limit, true)));
        await Assert.ThrowsAsync<DomainException>(() =>
            Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Access = new ClientAccessPolicy(limit, true) }));
        Assert.Equal(1000, new ClientAccessPolicy(1000, true).Validated().MaxDevices);
    }

    [Fact]
    public async Task Reissue_revokes_devices_that_signed_in_by_key_only()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        await AddDeviceAsync(database, issued.Client.Id, Device.ByKey);
        await AddDeviceAsync(database, issued.Client.Id, "google");
        await AddDeviceAsync(database, issued.Client.Id, Device.ByKey, TestData.Now.AddDays(-1));
        Assert.Equal(1, await Clients(database).CountKeyDevicesAsync(issued.Client.Id));

        var reissued = await Clients(database).ReissueAsync(issued.Client.Id);

        Assert.True(AccessToken.IsWellFormed(reissued.Token));
        Assert.Equal(1, reissued.DevicesRevoked);
        var devices = await database.NewContext().Devices.AsNoTracking().ToListAsync();
        Assert.Null(devices.Single(device => device.SignedInWith == "google").RevokedAt);
        var keyed = devices.Single(device => device.SignedInWith == Device.ByKey && device.RevokedReason != DeviceRevocation.Admin);
        Assert.Equal(DeviceRevocation.KeyReissued, keyed.RevokedReason);
        Assert.Equal(TestData.Now, keyed.RevokedAt);
        Assert.Equal(0, await Clients(database).CountKeyDevicesAsync(issued.Client.Id));
    }
}
