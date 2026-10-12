using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Tests;

public class InboundServiceTests
{
    private static InboundService Service(TestDatabase database, FakeVault? vault = null) =>
        new(database.NewContext(), vault ?? new FakeVault(), new FakeTimeProvider(TestData.Now));

    [Fact]
    public async Task Added_inbound_is_listed_with_defaults()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Service(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");

        var inbound = Assert.Single(await Service(database).ListAsync());
        Assert.Equal("echo", inbound.Slug);
        Assert.Equal("echo", inbound.Name);
        Assert.Equal("https://mcp.example.org/mcp", inbound.Url);
        Assert.Equal(InboundKind.Http, inbound.Kind);
        Assert.True(inbound.Enabled);
        Assert.False(inbound.ForwardIdentity);
        Assert.Null(inbound.UpstreamHeadersSealed);
        Assert.Equal(TestData.Now, inbound.CreatedAt);
        Assert.Equal(300, inbound.RequestTimeoutSeconds);
    }

    [Fact]
    public async Task Upstream_headers_are_stored_sealed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var vault = new FakeVault();

        await Service(database, vault).AddHttpAsync(
            "echo", "Эхо", "https://mcp.example.org/mcp", [KeyValuePair.Create("X-Api-Key", "s3cret")], forwardIdentity: true);

        var inbound = Assert.Single(await Service(database).ListAsync());
        Assert.Equal("Эхо", inbound.Name);
        Assert.True(inbound.ForwardIdentity);
        Assert.DoesNotContain("s3cret", inbound.UpstreamHeadersSealed);
        Assert.Equal("s3cret", UpstreamHeaders.Open(vault, inbound.UpstreamHeadersSealed)!["X-Api-Key"]);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("mcp.example.org/mcp")]
    [InlineData("ftp://mcp.example.org/mcp")]
    [InlineData("https://user:pass@mcp.example.org/mcp")]
    [InlineData("https://mcp.example.org/mcp#part")]
    [InlineData("")]
    public async Task Bad_url_is_rejected(string url)
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<DomainException>(() => Service(database).AddHttpAsync("echo", null, url));
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected_with_a_clear_message()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Service(database).AddHttpAsync("echo", null, "https://a.example.org/mcp");

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Service(database).AddHttpAsync("echo", null, "https://b.example.org/mcp"));

        Assert.Contains("echo", error.Message);
        Assert.Contains("уже есть", error.Message);
    }

    [Fact]
    public async Task Inbound_can_be_disabled_and_enabled_again()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Service(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");

        await Service(database).SetEnabledAsync("echo", false);
        Assert.False(Assert.Single(await Service(database).ListAsync()).Enabled);

        await Service(database).SetEnabledAsync("echo", true);
        Assert.True(Assert.Single(await Service(database).ListAsync()).Enabled);
    }

    [Fact]
    public async Task Removed_inbound_disappears()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Service(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");

        await Service(database).RemoveAsync("echo");

        Assert.Empty(await Service(database).ListAsync());
    }

    [Fact]
    public async Task Unknown_slug_is_reported()
    {
        await using var database = await TestDatabase.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => Service(database).RemoveAsync("nope"));

        Assert.Contains("не найден", error.Message);
    }

    [Fact]
    public async Task Each_change_bumps_the_routing_revision()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Service(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        await Service(database).SetEnabledAsync("echo", false);

        await using var db = database.NewContext();
        Assert.Equal(2, await db.RoutingStates.Select(s => s.Revision).SingleAsync());
    }
}
