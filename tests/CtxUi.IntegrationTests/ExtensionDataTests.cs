using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Enfilade;
using CtxUi.Core;
using CtxUi.Extensions;

namespace CtxUi.IntegrationTests;

public class ExtensionDataTests
{
    private const string ToolCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private static IPanelData Data(GatewayHost gateway)
    {
        return gateway.App.Services.GetRequiredService<IPanelData>();
    }

    [Fact]
    public async Task Data_lists_clients_inbounds_and_usage()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        var inbound = await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        var data = Data(gateway);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var client = Assert.Single(await data.ClientsAsync());
        Assert.Equal(issued.Client.Id, client.Id);
        Assert.Equal("Иванов", client.Name);
        Assert.True(client.Enabled);
        Assert.Equal(client.Name, (await data.ClientAsync(client.Id)).Name);
        Assert.Equal("echo", Assert.Single(await data.InboundsAsync()).Slug);

        await Eventually.TrueAsync(
            async () =>
            {
                await gateway.FlushUsageAsync();
                return (await data.UsageDaysAsync(today.AddDays(-1), today.AddDays(1))).Count == 1;
            },
            "использование дошло до данных панели");
        var day = Assert.Single(await data.UsageDaysAsync(today.AddDays(-1), today.AddDays(1), client.Id));
        Assert.Equal(inbound.Id, day.InboundId);
        Assert.Equal(1, day.ToolCalls);
        Assert.Empty(await data.UsageDaysAsync(today.AddDays(-1), today.AddDays(1), Guid.NewGuid()));
        Assert.Empty(await data.UsageDaysAsync(today.AddDays(1), today.AddDays(2)));
    }

    [Fact]
    public async Task Disabling_a_client_through_data_takes_effect_in_the_gateway()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        await Data(gateway).SetClientEnabledAsync(issued.Client.Id, false);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await Data(gateway).ClientAsync(issued.Client.Id)).Enabled);
    }

    [Fact]
    public async Task Disabling_an_inbound_through_data_takes_effect_in_the_gateway()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        await Data(gateway).SetInboundEnabledAsync("echo", false);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Added_inbound_takes_calls_of_linked_client_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("other", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["other"]);
        var data = Data(gateway);

        var inbound = await data.AddHttpInboundAsync(new HttpInboundDraft("built", "Built", upstream.McpUrl,
            new Dictionary<string, string> { ["X-Route-Secret"] = "s" }));
        await data.SetInboundClientsAsync("built", [issued.Client.Id]);

        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/built", issued.Token, ToolCall));
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Equal("s", upstream.LastHeader("X-Route-Secret"));
        Assert.Equal([issued.Client.Id], await data.InboundClientsAsync("built"));
        Assert.Equal("built", inbound.Slug);
        Assert.Equal("Built", inbound.Name);
    }

    [Fact]
    public async Task Setting_inbound_clients_keeps_their_other_inbounds()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("a", upstream.McpUrl);
        await gateway.AddInboundAsync("b", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["a"]);
        var data = Data(gateway);

        await data.SetInboundClientsAsync("b", [issued.Client.Id]);
        await data.SetInboundClientsAsync("b", []);

        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/a", issued.Token, ToolCall));
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Empty(await data.InboundClientsAsync("b"));
        Assert.Equal([issued.Client.Id], await data.InboundClientsAsync("a"));
    }

    [Fact]
    public async Task Setting_inbound_clients_rejects_an_unknown_client()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("a", upstream.McpUrl);

        var failure =
            await Assert.ThrowsAsync<NotFoundException>(() => Data(gateway).SetInboundClientsAsync("a", [Guid.NewGuid()]));
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Fact]
    public async Task Removed_inbound_stops_taking_calls()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var data = Data(gateway);

        await data.RemoveInboundAsync("echo");

        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        Assert.NotEqual(HttpStatusCode.OK, call.StatusCode);
        Assert.Empty(await data.InboundsAsync());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Adding_a_taken_slug_fails_with_a_reason_the_panel_shows()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);

        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            Data(gateway).AddHttpInboundAsync(new HttpInboundDraft("echo", "Echo", upstream.McpUrl)));

        await using var scope = gateway.App.Services.CreateAsyncScope();
        var shown = scope.ServiceProvider.GetRequiredService<ErrorMessages>().Of(failure);
        Assert.Equal(Words.Sentence(failure.Message), shown);
        Assert.Contains("echo", shown);
    }

    [Fact]
    public async Task Adding_an_inbound_with_an_invalid_address_throws_the_core_failure()
    {
        await using var gateway = await GatewayHost.StartAsync();

        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            Data(gateway).AddHttpInboundAsync(new HttpInboundDraft("echo", "Echo", "not an address")));
        Assert.Contains("not an address", failure.Message);
    }

    [Fact]
    public async Task Removing_an_unknown_inbound_throws_the_core_failure()
    {
        await using var gateway = await GatewayHost.StartAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Data(gateway).RemoveInboundAsync("missing"));
    }
}
