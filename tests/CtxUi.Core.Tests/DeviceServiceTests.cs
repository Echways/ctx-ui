using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class DeviceServiceTests
{
    private static readonly DeviceSeen Seen = new("Chrome · Windows", "203.0.113.7", Device.ByKey);

    private static readonly string Marker = DeviceMarker.Generate();

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);
    private Guid _clientId;

    private DeviceService Devices(TestDatabase database) => new(database.NewContext(), _clock);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private static Task<long> RevisionAsync(TestDatabase database) =>
        database.NewContext().RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    private async Task<TestDatabase> ClientWithLimitAsync(int? limit)
    {
        var database = await TestDatabase.CreateAsync();
        await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        _clientId = (await Clients(database).AddAsync("Иванов", ["echo"], access: new ClientAccessPolicy(limit, true))).Client.Id;
        return database;
    }

    [Fact]
    public void Marker_is_long_and_stored_as_a_hash()
    {
        var marker = DeviceMarker.Generate();

        Assert.True(DeviceMarker.IsWellFormed(marker));
        Assert.StartsWith("dvc_", marker);
        Assert.Equal(47, marker.Length);
        Assert.NotEqual(marker, DeviceMarker.Hash(marker));
        Assert.False(DeviceMarker.IsWellFormed("dvc_short"));
    }

    [Fact]
    public async Task Same_browser_is_one_device()
    {
        await using var database = await ClientWithLimitAsync(2);

        var first = await Devices(database).AdmitAsync(_clientId, Marker, Seen);
        var again = await Devices(database).AdmitAsync(_clientId, Marker, Seen);

        Assert.Equal(first.Device!.Id, again.Device!.Id);
        Assert.Equal(1, await Devices(database).ActiveCountAsync(_clientId));
        var saved = await Devices(database).GetAsync(first.Device.Id);
        Assert.Equal(DeviceMarker.Hash(Marker), saved.MarkerHash);
        Assert.Equal(("Chrome · Windows", "203.0.113.7", Device.ByKey, TestData.Now), (saved.Name, saved.SignInIp, saved.SignedInWith, saved.CreatedAt));
    }

    [Fact]
    public async Task Third_browser_over_a_limit_of_two_is_refused_with_the_active_list()
    {
        await using var database = await ClientWithLimitAsync(2);
        await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen);
        await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen);

        var third = await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen);

        Assert.Null(third.Device);
        Assert.Equal(2, third.Active.Count);
        Assert.Equal(2, await Devices(database).ActiveCountAsync(_clientId));
    }

    [Fact]
    public async Task Without_a_limit_every_browser_is_let_in()
    {
        await using var database = await ClientWithLimitAsync(null);

        for (var i = 0; i < 5; i++)
            Assert.NotNull((await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device);

        Assert.Equal(5, await Devices(database).ActiveCountAsync(_clientId));
    }

    [Fact]
    public async Task Replacing_revokes_the_chosen_device_as_user()
    {
        await using var database = await ClientWithLimitAsync(1);
        var old = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        var marker = DeviceMarker.Generate();

        var fresh = await Devices(database).ReplaceAsync(_clientId, old.Id, marker, Seen with { SignedInWith = "google" });

        var replaced = await Devices(database).GetAsync(old.Id);
        Assert.Equal(DeviceRevocation.User, replaced.RevokedReason);
        Assert.Equal(TestData.Now, replaced.RevokedAt);
        Assert.Equal("google", fresh.SignedInWith);
        Assert.True(await Devices(database).IsActiveAsync(fresh.Id));
        Assert.False(await Devices(database).IsActiveAsync(old.Id));
        Assert.Equal(fresh.Id, (await Devices(database).AdmitAsync(_clientId, marker, Seen)).Device!.Id);
    }

    [Fact]
    public async Task Replacing_another_clients_device_is_not_found()
    {
        await using var database = await ClientWithLimitAsync(1);
        var other = (await Clients(database).AddAsync("Петров", ["echo"])).Client.Id;
        var foreign = (await Devices(database).AdmitAsync(other, DeviceMarker.Generate(), Seen)).Device!;

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Devices(database).ReplaceAsync(_clientId, foreign.Id, DeviceMarker.Generate(), Seen));
        Assert.True(await Devices(database).IsActiveAsync(foreign.Id));
    }

    [Fact]
    public async Task Replacing_a_revoked_device_is_not_found()
    {
        await using var database = await ClientWithLimitAsync(1);
        var old = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        await Devices(database).RevokeAsync(_clientId, old.Id, DeviceRevocation.Admin);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Devices(database).ReplaceAsync(_clientId, old.Id, DeviceMarker.Generate(), Seen));
    }

    [Fact]
    public async Task Lowering_the_limit_keeps_existing_devices()
    {
        await using var database = await ClientWithLimitAsync(3);
        for (var i = 0; i < 3; i++)
            await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen);

        await Clients(database).UpdateAsync(_clientId, new ClientUpdate { Access = new ClientAccessPolicy(1, true) });

        Assert.Equal(3, await Devices(database).ActiveCountAsync(_clientId));
        Assert.Null((await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device);
    }

    [Fact]
    public async Task Idle_device_stops_counting_and_is_revoked_by_the_sweep()
    {
        await using var database = await ClientWithLimitAsync(1);
        var idle = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;

        _clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(0, await Devices(database).ActiveCountAsync(_clientId));
        Assert.False(await Devices(database).IsActiveAsync(idle.Id));
        Assert.Equal(1, await Devices(database).RevokeIdleAsync());
        var revoked = await Devices(database).GetAsync(idle.Id);
        Assert.Equal(DeviceRevocation.Idle, revoked.RevokedReason);
        Assert.Equal(0, await Devices(database).RevokeIdleAsync());
    }

    [Fact]
    public async Task Recent_use_keeps_a_device_active()
    {
        await using var database = await ClientWithLimitAsync(1);
        var device = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;
        _clock.Advance(TimeSpan.FromDays(20));
        await Devices(database).MarkUsedAsync([KeyValuePair.Create(device.Id, _clock.GetUtcNow())]);

        _clock.Advance(TimeSpan.FromDays(20));

        Assert.True(await Devices(database).IsActiveAsync(device.Id));
        Assert.Equal(0, await Devices(database).RevokeIdleAsync());
        Assert.Equal(TestData.Now.AddDays(20), (await Devices(database).GetAsync(device.Id)).LastUsedAt);
    }

    [Fact]
    public async Task Marking_used_does_not_bump_the_revision()
    {
        await using var database = await ClientWithLimitAsync(1);
        var device = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;
        var revision = await RevisionAsync(database);

        await Devices(database).MarkUsedAsync([KeyValuePair.Create(device.Id, TestData.Now.AddMinutes(5))]);

        Assert.Equal(revision, await RevisionAsync(database));
    }

    [Fact]
    public async Task Revoked_device_with_the_same_marker_gets_a_new_device()
    {
        await using var database = await ClientWithLimitAsync(2);
        var first = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;
        await Devices(database).RevokeAsync(_clientId, first.Id, DeviceRevocation.Admin);

        var second = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, await Devices(database).ActiveCountAsync(_clientId));
    }

    [Fact]
    public async Task Same_marker_for_another_client_is_another_device()
    {
        await using var database = await ClientWithLimitAsync(1);
        var other = (await Clients(database).AddAsync("Петров", ["echo"])).Client.Id;

        var mine = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;
        var theirs = (await Devices(database).AdmitAsync(other, Marker, Seen)).Device!;

        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal(other, theirs.ClientId);
    }

    [Fact]
    public async Task Revoked_devices_show_within_the_window_with_reason()
    {
        await using var database = await ClientWithLimitAsync(null);
        var old = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        await Devices(database).RevokeAsync(_clientId, old.Id, DeviceRevocation.Admin);
        _clock.Advance(TimeSpan.FromDays(10));
        var recent = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        await Devices(database).RevokeAsync(_clientId, recent.Id, DeviceRevocation.TokenReused);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var active = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        await Devices(database).AddAppAsync(active.Id, "dcr:cursor", "Cursor", "authorization-1");

        var listed = await Devices(database).ListAsync(_clientId, _clock.GetUtcNow().AddDays(-5));

        Assert.Equal([active.Id, recent.Id], listed.Select(device => device.Id));
        Assert.Equal(DeviceRevocation.TokenReused, listed[1].RevokedReason);
        Assert.Equal("Cursor", Assert.Single(listed[0].Apps).AppName);
    }

    [Fact]
    public async Task Apps_are_remembered_per_device()
    {
        await using var database = await ClientWithLimitAsync(null);
        var device = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;

        Assert.Null(await Devices(database).AppAuthorizationAsync(device.Id, "dcr:cursor"));
        await Devices(database).AddAppAsync(device.Id, "dcr:cursor", "Cursor", "authorization-1");
        await Devices(database).AddAppAsync(device.Id, "https://claude.ai/metadata", "Claude", "authorization-2");
        await Devices(database).AddAppAsync(device.Id, "dcr:cursor", "Cursor", "authorization-3");

        Assert.Equal("authorization-3", await Devices(database).AppAuthorizationAsync(device.Id, "dcr:cursor"));
        Assert.Equal(["authorization-2", "authorization-3"], (await Devices(database).AuthorizationIdsAsync(device.Id)).Order());
    }

    [Fact]
    public async Task Revoking_another_clients_device_is_not_found()
    {
        await using var database = await ClientWithLimitAsync(null);
        var other = (await Clients(database).AddAsync("Петров", ["echo"])).Client.Id;
        var foreign = (await Devices(database).AdmitAsync(other, Marker, Seen)).Device!;

        await Assert.ThrowsAsync<NotFoundException>(() => Devices(database).RevokeAsync(_clientId, foreign.Id, DeviceRevocation.Admin));
    }

    [Fact]
    public async Task Revoking_bumps_the_revision_and_keeps_the_first_reason()
    {
        await using var database = await ClientWithLimitAsync(null);
        var device = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;
        var revision = await RevisionAsync(database);

        await Devices(database).RevokeAsync(_clientId, device.Id, DeviceRevocation.User);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Devices(database).RevokeAsync(_clientId, device.Id, DeviceRevocation.Admin);

        Assert.Equal(revision + 1, await RevisionAsync(database));
        var revoked = await Devices(database).GetAsync(device.Id);
        Assert.Equal((DeviceRevocation.User, TestData.Now), (revoked.RevokedReason!.Value, revoked.RevokedAt!.Value));
    }

    [Fact]
    public async Task Revoke_all_counts_only_active_devices()
    {
        await using var database = await ClientWithLimitAsync(null);
        var revoked = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        await Devices(database).RevokeAsync(_clientId, revoked.Id, DeviceRevocation.User);
        await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen);
        await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen);

        Assert.Equal(2, await Devices(database).RevokeAllAsync(_clientId, DeviceRevocation.Admin));
        Assert.Equal(0, await Devices(database).ActiveCountAsync(_clientId));
        Assert.Equal(DeviceRevocation.User, (await Devices(database).GetAsync(revoked.Id)).RevokedReason);
    }

    [Fact]
    public async Task Prune_removes_devices_revoked_long_ago_with_their_apps()
    {
        await using var database = await ClientWithLimitAsync(null);
        var old = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;
        await Devices(database).AddAppAsync(old.Id, "dcr:cursor", "Cursor", "authorization-1");
        await Devices(database).RevokeAsync(_clientId, old.Id, DeviceRevocation.User);
        _clock.Advance(TimeSpan.FromDays(40));
        var kept = (await Devices(database).AdmitAsync(_clientId, DeviceMarker.Generate(), Seen)).Device!;

        Assert.Equal(1, await Devices(database).PruneAsync(_clock.GetUtcNow().AddDays(-30)));

        Assert.Equal([kept.Id], (await database.NewContext().Devices.AsNoTracking().ToListAsync()).Select(device => device.Id));
        Assert.Empty(await database.NewContext().DeviceApps.AsNoTracking().ToListAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => Devices(database).GetAsync(old.Id));
    }

    [Fact]
    public async Task Names_map_devices_to_their_names()
    {
        await using var database = await ClientWithLimitAsync(null);
        var device = (await Devices(database).AdmitAsync(_clientId, Marker, Seen)).Device!;

        Assert.Equal("Chrome · Windows", (await Devices(database).NamesAsync())[device.Id]);
    }
}
