using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Usage;

namespace CtxUi.IntegrationTests;

public class UpstreamFailureTests
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private static readonly ClientLimits OneCall = new(QuotaCalls: 1, ResetEveryDays: 30);

    private static Task<List<UsageEvent>> JournalAsync(GatewayHost gateway) =>
        gateway.ChangeAsync(services => services.GetRequiredService<UsageStore>().RecentAsync(null, 100));

    [Fact]
    public async Task Upstream_silent_longer_than_the_inbound_timeout_gets_504_and_the_call_is_refunded()
    {
        await using var upstream = await FakeUpstream.StartAsync((app, _) => app.Map("/slow", async context =>
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted)));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.ArrangeAsync(services => services.GetRequiredService<InboundService>().AddHttpAsync(
            "slow", new InboundSettings { Url = upstream.BaseUrl + "/slow", RequestTimeoutSeconds = 1 }));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["slow", "echo"], limits: OneCall);

        using var timedOut = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/slow", issued.Token, ToolCall));
        await gateway.FlushUsageAsync();
        var journal = await JournalAsync(gateway);
        using var next = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));

        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Equal(UsageOutcome.UpstreamFailed, Assert.Single(journal).Outcome);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task Body_cut_off_by_the_client_is_neither_charged_nor_journaled()
    {
        var logs = new CapturingLogs();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: OneCall);

        var address = new Uri(gateway.BaseUrl);
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(address.Host, address.Port);
            var head = "POST /mcp/echo HTTP/1.1\r\n" +
                $"Host: {address.Authority}\r\n" +
                $"Authorization: Bearer {issued.Token}\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: 1000\r\n\r\n" +
                ToolCall[..10];
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
        }

        await Eventually.TrueAsync(
            () => Task.FromResult(logs.Lines.Any(line => line.Contains(nameof(UsageOutcome.ClientAborted)))),
            "gateway handled the cut-off request");
        await gateway.FlushUsageAsync();
        var journal = await JournalAsync(gateway);
        using var next = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));

        Assert.Empty(journal);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Single(upstream.Requests);
    }
}
