using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;

namespace CtxUi.Core.Tests;

public class RoutingSnapshotLoaderTests
{
    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private InboundService Inbounds(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private RoutingSnapshotLoader Loader(TestDatabase database) =>
        new(database.NewContext(), _vault, NullLogger<RoutingSnapshotLoader>.Instance);

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
        var decision = snapshot.Authorize(issued.Token, "echo", TestData.Now);
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

        Assert.Equal(AccessStatus.InboundNotFound, snapshot.Authorize(issued.Token, "echo", TestData.Now).Status);
    }

    [Fact]
    public async Task Disabled_client_stays_in_the_snapshot_so_the_refusal_can_say_why()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        await Clients(database).SetEnabledAsync("Иванов", false);

        var snapshot = await Loader(database).LoadAsync();

        Assert.Equal(AccessStatus.ClientDisabled, snapshot.Authorize(issued.Token, "echo", TestData.Now).Status);
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

        Assert.Equal(AccessStatus.InboundNotFound, snapshot.Authorize(issued.Token, "secret", TestData.Now).Status);
        Assert.Equal(AccessStatus.Allowed, snapshot.Authorize(issued.Token, "plain", TestData.Now).Status);
    }
}
