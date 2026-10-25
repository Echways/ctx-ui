using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;

namespace CtxUi.Core.Tests;

public class ClientMetadataUpdateTests
{
    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private async Task<(TestDatabase Database, IssuedClient Issued)> DatabaseWithClientAsync()
    {
        var database = await TestDatabase.CreateAsync();
        await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var issued = await Clients(database).AddAsync(
            "Иванов", ["echo"], metadata: [KeyValuePair.Create("group", "4419"), KeyValuePair.Create("storage-mb", "50")]);
        return (database, issued);
    }

    [Fact]
    public async Task Named_metadata_replaces_all_pairs_and_reaches_the_routing_table()
    {
        var (database, issued) = await DatabaseWithClientAsync();
        await using var disposing = database;

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Metadata = [KeyValuePair.Create("group", "4420")] });

        var stored = (await Clients(database).GetAsync("Иванов")).Metadata;
        Assert.Equal("4420", Assert.Single(stored).Value);
        var snapshot = await new RoutingSnapshotLoader(database.NewContext(), _vault, _clock, NullLogger<RoutingSnapshotLoader>.Instance).LoadAsync();
        Assert.Equal("4420", Assert.Single(snapshot.Authorize(Credential.FromKey(issued.Token), "echo", TestData.Now).Client!.Metadata).Value);
    }

    [Fact]
    public async Task Update_that_does_not_name_metadata_keeps_it()
    {
        var (database, issued) = await DatabaseWithClientAsync();
        await using var disposing = database;

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Limits = new ClientLimits(QuotaCalls: 5) });

        Assert.Equal(2, (await Clients(database).GetAsync("Иванов")).Metadata.Count);
    }

    [Fact]
    public async Task Empty_metadata_removes_every_pair()
    {
        var (database, issued) = await DatabaseWithClientAsync();
        await using var disposing = database;

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Metadata = [] });

        Assert.Empty((await Clients(database).GetAsync("Иванов")).Metadata);
    }

    [Fact]
    public async Task Metadata_that_breaks_the_rules_is_refused_together_with_the_rest_of_the_update()
    {
        var (database, issued) = await DatabaseWithClientAsync();
        await using var disposing = database;
        var update = new ClientUpdate
        {
            Limits = new ClientLimits(QuotaCalls: 5),
            Metadata = [KeyValuePair.Create("плохой ключ", "1")],
        };

        await Assert.ThrowsAsync<DomainException>(() => Clients(database).UpdateAsync(issued.Client.Id, update));

        var client = await Clients(database).GetAsync("Иванов");
        Assert.Equal(2, client.Metadata.Count);
        Assert.Null(client.QuotaCalls);
    }
}
