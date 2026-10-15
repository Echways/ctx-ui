using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;

namespace CtxUi.Core.Tests;

public class InboundEditTests
{
    private const string Url = "https://mcp.example.org/mcp";

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private InboundService Inbounds(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private static Task<long> RevisionAsync(TestDatabase database) =>
        database.NewContext().RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    private static InboundSettings Settings(string url = Url) => new() { Url = url };

    [Fact]
    public async Task Inbound_can_be_added_with_its_own_timeout_and_body_limit()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Inbounds(database).AddHttpAsync(
            "echo", Settings() with { Name = "Эхо", RequestTimeoutSeconds = 30, MaxBodyBytes = 65_536, ForwardIdentity = true });

        var inbound = await Inbounds(database).GetAsync("echo");
        Assert.Equal("Эхо", inbound.Name);
        Assert.Equal(30, inbound.RequestTimeoutSeconds);
        Assert.Equal(65_536, inbound.MaxBodyBytes);
        Assert.True(inbound.ForwardIdentity);
    }

    [Fact]
    public async Task Settings_left_alone_take_the_defaults()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Inbounds(database).AddHttpAsync("echo", Settings());

        var inbound = await Inbounds(database).GetAsync("echo");
        Assert.Equal("echo", inbound.Name);
        Assert.Equal(Inbound.DefaultRequestTimeoutSeconds, inbound.RequestTimeoutSeconds);
        Assert.Equal(Inbound.DefaultMaxBodyBytes, inbound.MaxBodyBytes);
        Assert.Null(inbound.UpstreamHeadersSealed);
    }

    [Fact]
    public async Task Update_replaces_what_the_form_shows_and_bumps_the_revision()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url);

        await Inbounds(database).UpdateAsync("echo", new InboundSettings
        {
            Name = " Учебный ",
            Url = "http://10.0.0.5:9000/mcp?tenant=7",
            ForwardIdentity = true,
            RequestTimeoutSeconds = 3_600,
            MaxBodyBytes = 1_024,
        });

        var inbound = await Inbounds(database).GetAsync("echo");
        Assert.Equal("Учебный", inbound.Name);
        Assert.Equal("http://10.0.0.5:9000/mcp?tenant=7", inbound.Url);
        Assert.True(inbound.ForwardIdentity);
        Assert.Equal(3_600, inbound.RequestTimeoutSeconds);
        Assert.Equal(1_024, inbound.MaxBodyBytes);
        Assert.True(inbound.Enabled);
        Assert.Equal(TestData.Now, inbound.CreatedAt);
        Assert.Equal(2, await RevisionAsync(database));
    }

    [Fact]
    public async Task Blank_name_falls_back_to_the_slug()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", "Эхо", Url);

        await Inbounds(database).UpdateAsync("echo", Settings() with { Name = "  " });

        Assert.Equal("echo", (await Inbounds(database).GetAsync("echo")).Name);
    }

    [Fact]
    public async Task Update_that_does_not_name_headers_keeps_the_stored_ones()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url, [KeyValuePair.Create("X-Api-Key", "s3cret")]);

        await Inbounds(database).UpdateAsync("echo", Settings("https://other.example.org/mcp"));

        var inbound = await Inbounds(database).GetAsync("echo");
        Assert.Equal("s3cret", UpstreamHeaders.Open(_vault, inbound.UpstreamHeadersSealed)!["X-Api-Key"]);
    }

    [Fact]
    public async Task Update_that_names_headers_replaces_all_of_them()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url, [KeyValuePair.Create("X-Api-Key", "s3cret")]);

        await Inbounds(database).UpdateAsync(
            "echo", Settings() with { UpstreamHeaders = [KeyValuePair.Create("Authorization", "Bearer new")] });

        var inbound = await Inbounds(database).GetAsync("echo");
        var headers = UpstreamHeaders.Open(_vault, inbound.UpstreamHeadersSealed)!;
        Assert.Equal("Bearer new", Assert.Single(headers).Value);
        Assert.DoesNotContain("Bearer new", inbound.UpstreamHeadersSealed);
    }

    [Fact]
    public async Task Update_with_an_empty_header_list_removes_them()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url, [KeyValuePair.Create("X-Api-Key", "s3cret")]);

        await Inbounds(database).UpdateAsync("echo", Settings() with { UpstreamHeaders = [] });

        Assert.Null((await Inbounds(database).GetAsync("echo")).UpstreamHeadersSealed);
    }

    [Theory]
    [InlineData("ftp://mcp.example.org/mcp", 300, 4_194_304)]
    [InlineData("https://user:pass@mcp.example.org/mcp", 300, 4_194_304)]
    [InlineData(Url, 0, 4_194_304)]
    [InlineData(Url, 3_601, 4_194_304)]
    [InlineData(Url, 300, 1_023)]
    [InlineData(Url, 300, 67_108_865)]
    public async Task Setting_outside_the_rules_is_refused_and_nothing_changes(string url, int timeout, int bodyBytes)
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", "Эхо", Url);
        var settings = new InboundSettings { Name = "Другое", Url = url, RequestTimeoutSeconds = timeout, MaxBodyBytes = bodyBytes };

        await Assert.ThrowsAsync<DomainException>(() => Inbounds(database).UpdateAsync("echo", settings));
        await Assert.ThrowsAsync<DomainException>(() => Inbounds(database).AddHttpAsync("second", settings));

        var inbound = Assert.Single(await Inbounds(database).ListAsync());
        Assert.Equal("Эхо", inbound.Name);
        Assert.Equal(Url, inbound.Url);
        Assert.Equal(1, await RevisionAsync(database));
    }

    [Fact]
    public async Task Header_that_breaks_the_rules_is_refused_on_update()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url);

        var exception = await Assert.ThrowsAsync<DomainException>(() => Inbounds(database).UpdateAsync(
            "echo", Settings() with { UpstreamHeaders = [KeyValuePair.Create("X-Mcp-Client-Id", "spoofed")] }));

        Assert.Contains("X-Mcp-Client-Id", exception.Message);
    }

    [Fact]
    public async Task Unknown_inbound_is_reported_by_name()
    {
        await using var database = await TestDatabase.CreateAsync();

        var onGet = await Assert.ThrowsAsync<DomainException>(() => Inbounds(database).GetAsync("nope"));
        var onUpdate = await Assert.ThrowsAsync<DomainException>(() => Inbounds(database).UpdateAsync("nope", Settings()));

        Assert.Contains("nope", onGet.Message);
        Assert.Contains("nope", onUpdate.Message);
    }

    [Fact]
    public async Task Header_names_can_be_listed_without_their_values()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync(
            "echo", null, Url, [KeyValuePair.Create("X-Api-Key", "s3cret"), KeyValuePair.Create("Authorization", "Bearer t")]);
        await Inbounds(database).AddHttpAsync("plain", null, Url);

        var named = Inbounds(database).HeaderNames(await Inbounds(database).GetAsync("echo"));
        var none = Inbounds(database).HeaderNames(await Inbounds(database).GetAsync("plain"));

        Assert.Equal(["Authorization", "X-Api-Key"], named);
        Assert.Empty(none!);
    }

    [Fact]
    public async Task Header_names_are_unknown_when_the_keys_are_lost()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url, [KeyValuePair.Create("X-Api-Key", "s3cret")]);
        _vault.Broken = true;

        Assert.Null(Inbounds(database).HeaderNames(await Inbounds(database).GetAsync("echo")));
    }

    [Fact]
    public async Task Clients_are_counted_per_inbound()
    {
        await using var database = await TestDatabase.CreateAsync();
        var echo = await Inbounds(database).AddHttpAsync("echo", null, Url);
        var idle = await Inbounds(database).AddHttpAsync("idle", null, Url);
        var clients = new ClientService(database.NewContext(), _vault, _clock);
        await clients.AddAsync("Иванов", ["echo"]);
        await clients.AddAsync("Петров", ["echo", "idle"]);

        var counts = await Inbounds(database).ClientCountsAsync();

        Assert.Equal(2, counts[echo.Id]);
        Assert.Equal(1, counts[idle.Id]);
    }

    [Fact]
    public async Task Edited_inbound_reaches_the_routing_table()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, Url);
        var issued = await new ClientService(database.NewContext(), _vault, _clock).AddAsync("Иванов", ["echo"]);

        await Inbounds(database).UpdateAsync("echo", new InboundSettings
        {
            Url = "https://other.example.org/mcp",
            RequestTimeoutSeconds = 30,
            MaxBodyBytes = 2_048,
            UpstreamHeaders = [KeyValuePair.Create("X-Api-Key", "s3cret")],
        });

        var snapshot = await new RoutingSnapshotLoader(database.NewContext(), _vault, NullLogger<RoutingSnapshotLoader>.Instance).LoadAsync();
        var route = snapshot.Authorize(issued.Token, "echo", TestData.Now).Route!;
        Assert.Equal(new Uri("https://other.example.org/mcp"), route.Url);
        Assert.Equal(TimeSpan.FromSeconds(30), route.RequestTimeout);
        Assert.Equal(2_048, route.MaxBodyBytes);
        Assert.Equal("s3cret", route.UpstreamHeaders["X-Api-Key"]);
    }
}
