using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class RoutingSnapshotLoaderTests
{
    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private InboundService Inbounds(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private RoutingSnapshotLoader Loader(TestDatabase database) =>
        new(database.NewContext(), _vault, _clock, NullLogger<RoutingSnapshotLoader>.Instance);

    [Fact]
    public async Task Snapshot_carries_routes_clients_and_the_revision_it_was_read_at()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync(
            "echo", null, "https://mcp.example.org/mcp?tenant=7", [KeyValuePair.Create("X-Api-Key", "s3cret")], forwardIdentity: true);
        var issued = await Clients(database).AddAsync(
            "Иванов", ["echo"], TestData.Now.AddDays(1), [KeyValuePair.Create("group", "4419")]);

        var snapshot = await Loader(database).LoadAsync();

        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(2, await Loader(database).CurrentRevisionAsync());
        var decision = snapshot.Authorize(Credential.FromKey(issued.Token), "echo", TestData.Now);
        Assert.Equal(AccessStatus.Allowed, decision.Status);
        Assert.Equal(new Uri("https://mcp.example.org/mcp?tenant=7"), decision.Route!.Url);
        Assert.Equal("s3cret", decision.Route.UpstreamHeaders["X-Api-Key"]);
        Assert.True(decision.Route.ForwardIdentity);
        Assert.Equal(TimeSpan.FromSeconds(300), decision.Route.RequestTimeout);
        Assert.Equal(issued.Client.Id, decision.Client!.Id);
        Assert.Equal("4419", decision.Client.Metadata["group"]);
    }

    [Fact]
    public async Task Disabled_inbound_is_left_out()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        await Inbounds(database).SetEnabledAsync("echo", false);

        var snapshot = await Loader(database).LoadAsync();

        Assert.Equal(AccessStatus.InboundNotFound, snapshot.Authorize(Credential.FromKey(issued.Token), "echo", TestData.Now).Status);
    }

    [Fact]
    public async Task Disabled_client_stays_in_the_snapshot_so_the_refusal_can_say_why()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        await Clients(database).SetEnabledAsync(issued.Client.Id, false);

        var snapshot = await Loader(database).LoadAsync();

        Assert.Equal(AccessStatus.ClientDisabled, snapshot.Authorize(Credential.FromKey(issued.Token), "echo", TestData.Now).Status);
    }

    [Fact]
    public async Task Inbound_with_unreadable_secrets_is_skipped_and_the_rest_keeps_working()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("secret", null, "https://a.example.org/mcp", [KeyValuePair.Create("X-Api-Key", "k")]);
        await Inbounds(database).AddHttpAsync("plain", null, "https://b.example.org/mcp");
        var issued = await Clients(database).AddAsync("Иванов", ["secret", "plain"]);
        _vault.Broken = true;

        var snapshot = await Loader(database).LoadAsync();

        Assert.Equal(AccessStatus.InboundNotFound, snapshot.Authorize(Credential.FromKey(issued.Token), "secret", TestData.Now).Status);
        Assert.Equal(AccessStatus.Allowed, snapshot.Authorize(Credential.FromKey(issued.Token), "plain", TestData.Now).Status);
    }

    [Fact]
    public async Task Snapshot_carries_active_devices_and_enabled_providers_only()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], access: new ClientAccessPolicy(5, false));
        var devices = new DeviceService(database.NewContext(), _clock);
        var seen = new DeviceSeen("Chrome · Windows", "203.0.113.7", Device.ByKey);
        var active = (await devices.AdmitAsync(issued.Client.Id, DeviceMarker.Generate(), seen)).Device!;
        var revoked = (await devices.AdmitAsync(issued.Client.Id, DeviceMarker.Generate(), seen)).Device!;
        await devices.RevokeAsync(issued.Client.Id, revoked.Id, DeviceRevocation.Admin);
        var settings = new SignInSettingsService(database.NewContext(), _vault);
        await settings.UpdateAsync(new SignInSettingsUpdate { Enabled = true, IdleDays = 10, SelfRevoke = false }, "https://mcp.example.org");
        await settings.SetProviderAsync(new ProviderSetup("github", "github-id", "github-secret"));
        await settings.SetProviderAsync(new ProviderSetup("google", "google-id", "google-secret"));
        await settings.SetProviderEnabledAsync("google", false);

        var snapshot = await Loader(database).LoadAsync();

        Assert.True(snapshot.IsDeviceActive(active.Id, issued.Client.Id));
        Assert.False(snapshot.IsDeviceActive(active.Id, Guid.NewGuid()));
        Assert.False(snapshot.IsDeviceActive(revoked.Id, issued.Client.Id));
        Assert.Equal((true, true, false, 10), (snapshot.SignIn.Enabled, snapshot.SignIn.KeySignIn, snapshot.SignIn.SelfRevoke, snapshot.SignIn.IdleDays));
        var provider = Assert.Single(snapshot.SignIn.Providers);
        Assert.Equal(new ProviderConfig("github", "github-id", "github-secret", null, null), provider);
        var client = snapshot.ClientById(issued.Client.Id)!;
        Assert.False(client.DirectKey);
        Assert.Equal(5, client.MaxDevices);
        Assert.Equal(issued.Client.Id, snapshot.ClientByToken(issued.Token)!.Id);
        Assert.Null(snapshot.ClientByToken("garbage"));

        _clock.Advance(TimeSpan.FromDays(11));
        Assert.False((await Loader(database).LoadAsync()).IsDeviceActive(active.Id, issued.Client.Id));
    }

    [Fact]
    public async Task Provider_with_an_unreadable_secret_is_skipped()
    {
        await using var database = await TestDatabase.CreateAsync();
        var settings = new SignInSettingsService(database.NewContext(), _vault);
        await settings.SetProviderAsync(new ProviderSetup("github", "github-id", "github-secret"));
        await settings.SetProviderAsync(new ProviderSetup("gitlab", "gitlab-id", "gitlab-secret"));
        await using (var db = database.NewContext())
        {
            (await db.SignInProviders.FindAsync("github"))!.SecretSealed = "broken";
            await db.SaveChangesAsync();
        }

        var snapshot = await Loader(database).LoadAsync();

        Assert.Equal("gitlab", Assert.Single(snapshot.SignIn.Providers).Name);
        Assert.False(snapshot.SignIn.Enabled);
    }
}
