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
        await Assert.ThrowsAsync<DomainException>(() => Clients(database).GetAsync("Петров"));
    }

    [Fact]
    public async Task Reissue_replaces_the_token()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        var replacement = await Clients(database).ReissueAsync("Иванов");

        Assert.NotEqual(issued.Token, replacement);
        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(AccessToken.Hash(replacement), client.TokenHash);
        Assert.Equal(AccessToken.DisplayPrefix(replacement), client.TokenPrefix);
        Assert.Equal(issued.Client.Id, client.Id);
    }

    [Fact]
    public async Task Reveal_returns_the_current_token()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);

        Assert.Equal(issued.Token, await Clients(database).RevealAsync("Иванов"));
    }

    [Fact]
    public async Task Reveal_returns_null_when_the_sealed_copy_cannot_be_opened()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Clients(database).AddAsync("Иванов", ["echo"]);
        _vault.Broken = true;

        Assert.Null(await Clients(database).RevealAsync("Иванов"));
    }

    [Fact]
    public async Task Client_can_be_disabled_and_removed()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        await Clients(database).AddAsync("Иванов", ["echo"]);

        await Clients(database).SetEnabledAsync("Иванов", false);
        Assert.False(Assert.Single(await Clients(database).ListAsync()).Enabled);

        await Clients(database).RemoveAsync("Иванов");
        Assert.Empty(await Clients(database).ListAsync());
    }

    [Fact]
    public async Task Update_replaces_the_inbound_set()
    {
        await using var database = await DatabaseWithInboundsAsync("echo", "docs", "files");
        await Clients(database).AddAsync("Иванов", ["echo", "docs"]);

        await Clients(database).UpdateAsync("Иванов", new ClientUpdate { InboundSlugs = ["docs", "files"] });

        var client = Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(new[] { "docs", "files" }, client.Inbounds.Select(link => link.Inbound!.Slug).Order().ToArray());
    }

    [Fact]
    public async Task Update_changes_expiry_only_when_asked()
    {
        await using var database = await DatabaseWithInboundsAsync("echo");
        var expires = TestData.Now.AddDays(30);
        await Clients(database).AddAsync("Иванов", ["echo"], expires);

        await Clients(database).UpdateAsync("Иванов", new ClientUpdate { InboundSlugs = ["echo"] });
        Assert.Equal(expires, Assert.Single(await Clients(database).ListAsync()).ExpiresAt);

        await Clients(database).UpdateAsync("Иванов", new ClientUpdate { ChangeExpiry = true, ExpiresAt = null });
        Assert.Null(Assert.Single(await Clients(database).ListAsync()).ExpiresAt);

        await Assert.ThrowsAsync<DomainException>(() => Clients(database).UpdateAsync(
            "Иванов", new ClientUpdate { ChangeExpiry = true, ExpiresAt = TestData.Now.AddDays(-1) }));
    }
}
