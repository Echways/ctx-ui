using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Usage;

namespace CtxUi.IntegrationTests;

public class TrafficTests
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private static async Task<UsageEvent> SingleEventAsync(GatewayHost gateway)
    {
        await gateway.FlushUsageAsync();
        return Assert.Single(await gateway.ChangeAsync(services => services.GetRequiredService<UsageStore>().RecentAsync(null, 10)));
    }

    [Fact]
    public async Task Call_keeps_the_status_and_the_bytes_both_ways()
    {
        var answer = new string('x', 2_048);
        await using var upstream = await FakeUpstream.StartAsync((app, _) =>
            app.MapPost("/sized", () => Results.Text(answer, "application/json")));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("sized", upstream.BaseUrl + "/sized");
        var issued = await gateway.AddClientAsync("Иванов", ["sized"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/sized", issued.Token, ToolCall));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var usage = await SingleEventAsync(gateway);
        Assert.Equal((200, Encoding.UTF8.GetByteCount(ToolCall), 2_048L), (usage.Status, usage.RequestBytes, usage.ResponseBytes));
    }

    [Fact]
    public async Task Upstream_that_does_not_answer_is_recorded_with_the_gateway_status()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("gone", "http://127.0.0.1:9/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["gone"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/gone", issued.Token, ToolCall));

        var usage = await SingleEventAsync(gateway);
        Assert.Equal((UsageOutcome.UpstreamFailed, (int)response.StatusCode, 0L), (usage.Outcome, usage.Status, usage.ResponseBytes));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Rate_refusals_are_counted_without_log_lines()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: ClientLimits.None with { RatePerMinute = 1 });

        using var first = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var second = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var third = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(UsageOutcome.Ok, (await SingleEventAsync(gateway)).Outcome);
        var refusal = await gateway.ChangeAsync(services => services.GetRequiredService<CtxUiDbContext>().UsageRefusals.AsNoTracking().SingleAsync());
        Assert.Equal((issued.Client.Id, UsageOutcome.RateLimited, 2), (refusal.ClientId, refusal.Outcome, refusal.Count));
    }
}
