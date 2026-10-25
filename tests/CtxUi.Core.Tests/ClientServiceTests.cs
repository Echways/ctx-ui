using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Tests;

public class ClientServiceTests
{
    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private async Task<TestDatabase> DatabaseWithInboundsAsync(params string[] slugs)
    {
        var database = await TestDatabase.CreateAsync();
        foreach (var slug in slugs)
            await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync(slug, null, "https://mcp.example.org/mcp");

        return database;
    }

    [Fact]
    public async Task Issued_client_gets_a_token_stored_only_as_hash_and_sealed_copy()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");

        var issued = await Clients(database).AddAsync("Иванов 4419", ["echo"]);

        Assert.True(AccessToken.IsWellFormed(issued.Token));
        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal("Иванов 4419", client.Name);
        Assert.Equal(AccessToken.Hash(issued.Token), client.TokenHash);
        Assert.Equal(AccessToken.DisplayPrefix(issued.Token), client.TokenPrefix);
        Assert.DoesNotContain(issued.Token, client.TokenSealed);
        Assert.Equal(TestData.Now, client.CreatedAt);
        Assert.True(client.Enabled);
        Assert.Null(client.ExpiresAt);
        Assert.Equal("echo", Assert.Single(client.Inbounds).Inbound!.Slug);
    }

    [Fact]
    public async Task Expiry_and_metadata_are_saved()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var expires = TestData.Now.AddDays(30);

        await Clients(database).AddAsync("Иванов", ["echo"], expires, [KeyValuePair.Create("storage-mb", "50")]);

        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(expires, client.ExpiresAt);
        Assert.Equal("50", client.Metadata["storage-mb"]);
    }

    [Fact]
    public async Task Same_name_in_another_case_is_rejected_for_cyrillic_too()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Clients(database).AddAsync("Иванов 4419", ["echo"]);

        var error = await Assert.ThrowsAsync<DomainException>(() => Clients(database).AddAsync("иванов 4419", ["echo"]));

        Assert.Contains("уже есть", error.Message);
    }

    [Fact]
    public async Task Unknown_inbound_is_named_in_the_error()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");

        var error = await Assert.ThrowsAsync<DomainException>(() => Clients(database).AddAsync("Иванов", ["echo", "nope"]));

        Assert.Contains("nope", error.Message);
        Assert.Empty(await Clients(database).ListAsync());
    }

    [Fact]
    public async Task Client_without_inbounds_is_rejected()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");

        await Assert.ThrowsAsync<DomainException>(() => Clients(database).AddAsync("Иванов", []));
    }

    [Fact]
    public async Task Expiry_in_the_past_is_rejected()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");

        await Assert.ThrowsAsync<DomainException>(() => Clients(database).AddAsync("Иванов", ["echo"], TestData.Now));
    }

    [Fact]
    public async Task Client_is_found_by_name_in_any_case()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Clients(database).AddAsync("Иванов", ["echo"]);

        Assert.Equal("Иванов", (await Clients(database).GetAsync(" иванов ")).Name);
        await Assert.ThrowsAsync<NotFoundException>(() => Clients(database).GetAsync("Петров"));
    }

    [Fact]
    public async Task Client_is_found_by_id_with_its_inbounds()
    {
        await using var database = await DatabaseWithInboundsAsync("echo", "learn");
        var issued = await Clients(database).AddAsync("Иванов", ["learn", "echo"]);

        var found = await Clients(database).GetAsync(issued.Client.Id);

        Assert.Equal("Иванов", found.Name);
        Assert.Equal(["echo", "learn"], found.Inbounds.Select(link => link.Inbound!.Slug).Order());
        await Assert.ThrowsAsync<NotFoundException>(() => Clients(database).GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Reissue_replaces_the_token()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        var replacement = (await Clients(database).ReissueAsync(issued.Client.Id)).Token;

        Assert.NotEqual(issued.Token, replacement);
        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(AccessToken.Hash(replacement), client.TokenHash);
        Assert.Equal(AccessToken.DisplayPrefix(replacement), client.TokenPrefix);
        Assert.Equal(issued.Client.Id, client.Id);
    }

    [Fact]
    public async Task Reveal_opens_the_stored_token()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        Assert.Equal(issued.Token, Clients(database).Reveal(await Clients(database).GetAsync("Иванов")));
    }

    [Fact]
    public async Task Reveal_with_lost_keys_gives_null()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Clients(database).AddAsync("Иванов", ["echo"]);
        _vault.Broken = true;

        Assert.Null(Clients(database).Reveal(await Clients(database).GetAsync("Иванов")));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("reset-quota")]
    [InlineData("enable")]
    [InlineData("remove")]
    [InlineData("reissue")]
    public async Task Unknown_id_is_reported_as_not_found(string change)
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Clients(database).AddAsync("Иванов", ["echo"]);
        var clients = Clients(database);
        var unknown = Guid.NewGuid();

        Func<Task> act = change switch
        {
            "update" => () => clients.UpdateAsync(unknown, new ClientUpdate { Limits = ClientLimits.None }),
            "reset-quota" => () => clients.ResetQuotaAsync(unknown),
            "enable" => () => clients.SetEnabledAsync(unknown, false),
            "remove" => () => clients.RemoveAsync(unknown),
            _ => () => clients.ReissueAsync(unknown),
        };

        await Assert.ThrowsAsync<NotFoundException>(act);
        Assert.True(Assert.Single(await Clients(database).ListAsync()).Enabled);
    }

    [Fact]
    public async Task Names_map_every_client_id_to_its_name()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var first = await Clients(database).AddAsync("Иванов", ["echo"]);
        var second = await Clients(database).AddAsync("Петров", ["echo"]);

        var names = await Clients(database).NamesAsync();

        Assert.Equal(
            new Dictionary<Guid, string> { [first.Client.Id] = "Иванов", [second.Client.Id] = "Петров" },
            names);
    }

    [Fact]
    public async Task Client_can_be_disabled_and_removed()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        await Clients(database).SetEnabledAsync(issued.Client.Id, false);
        Assert.False(Assert.Single(await Clients(database).ListAsync()).Enabled);

        await Clients(database).RemoveAsync(issued.Client.Id);
        Assert.Empty(await Clients(database).ListAsync());
    }

    [Fact]
    public async Task Update_replaces_the_inbound_set()
    {
        await using var database = await DatabaseWithInboundsAsync("echo", "docs", "files");
        var issued = await Clients(database).AddAsync("Иванов", ["echo", "docs"]);

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { InboundSlugs = ["docs", "files"] });

        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(new[] { "docs", "files" }, client.Inbounds.Select(link => link.Inbound!.Slug).Order().ToArray());
    }

    [Fact]
    public async Task Update_changes_expiry_only_when_asked()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var expires = TestData.Now.AddDays(30);
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], expires);

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { InboundSlugs = ["echo"] });
        Assert.Equal(expires, Assert.Single(await Clients(database).ListAsync()).ExpiresAt);

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { ChangeExpiry = true, ExpiresAt = null });
        Assert.Null(Assert.Single(await Clients(database).ListAsync()).ExpiresAt);

        await Assert.ThrowsAsync<DomainException>(() => Clients(database).UpdateAsync(
            issued.Client.Id, new ClientUpdate { ChangeExpiry = true, ExpiresAt = TestData.Now.AddDays(-1) }));
    }

    [Fact]
    public async Task First_use_sets_both_marks_and_later_use_moves_only_the_last_one()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        var later = TestData.Now.AddHours(1);

        await Clients(database).MarkUsedAsync([KeyValuePair.Create(issued.Client.Id, TestData.Now)]);
        await Clients(database).MarkUsedAsync([KeyValuePair.Create(issued.Client.Id, later)]);

        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(TestData.Now, client.FirstUsedAt);
        Assert.Equal(later, client.LastUsedAt);
    }

    [Fact]
    public async Task Marking_use_does_not_touch_the_routing_revision()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        await using var db = database.NewContext();
        var before = await db.RoutingStates.Select(state => state.Revision).SingleAsync();

        await Clients(database).MarkUsedAsync([KeyValuePair.Create(issued.Client.Id, TestData.Now)]);

        Assert.Equal(before, await db.RoutingStates.Select(state => state.Revision).SingleAsync());
    }

    [Fact]
    public async Task Marking_use_of_several_clients_keeps_the_first_use()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var first = await Clients(database).AddAsync("Иванов", ["echo"]);
        var second = await Clients(database).AddAsync("Петров", ["echo"]);
        var later = TestData.Now.AddHours(1);

        await Clients(database).MarkUsedAsync([KeyValuePair.Create(first.Client.Id, TestData.Now)]);
        await Clients(database).MarkUsedAsync(
            [KeyValuePair.Create(first.Client.Id, later), KeyValuePair.Create(second.Client.Id, later)]);

        var clients = (await Clients(database).ListAsync()).ToDictionary(client => client.Name);
        Assert.Equal((TestData.Now, later), (clients["Иванов"].FirstUsedAt, clients["Иванов"].LastUsedAt));
        Assert.Equal((later, later), (clients["Петров"].FirstUsedAt, clients["Петров"].LastUsedAt));
    }
}
