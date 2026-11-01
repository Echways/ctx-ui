using System.Net;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Extensions;

namespace CtxUi.IntegrationTests;

public class ExtensionDataTests
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private static IPanelData Data(GatewayHost gateway) => gateway.App.Services.GetRequiredService<IPanelData>();

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
}
