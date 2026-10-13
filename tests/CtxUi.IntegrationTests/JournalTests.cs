using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Usage;

namespace CtxUi.IntegrationTests;

public class JournalTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 12);

    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";
    private const string Ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

    private static Task<List<UsageEvent>> LinesAsync(GatewayHost gateway) =>
        gateway.ChangeAsync(services => services.GetRequiredService<UsageStore>().RecentAsync(null, 100));

    private static Task<List<DailyTotal>> TotalsAsync(GatewayHost gateway, Guid? clientId = null) =>
        gateway.ChangeAsync(services => services.GetRequiredService<UsageStore>().DailyAsync(clientId, Today.AddDays(-1)));

    private static Task WaitForLinesAsync(GatewayHost gateway, int count) => Eventually.TrueAsync(
        async () => (await LinesAsync(gateway)).Count >= count,
        $"в журнале появилось строк: {count}");

    private static async Task<GatewayHost> StartAtAsync(FakeTimeProvider clock) =>
        await GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));

    [Fact]
    public async Task Forwarded_call_is_written_with_who_where_what_and_how_it_ended()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAtAsync(new FakeTimeProvider(Start));
        var inbound = await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await WaitForLinesAsync(gateway, 1);

        var line = Assert.Single(await LinesAsync(gateway));
        Assert.Equal(issued.Client.Id, line.ClientId);
        Assert.Equal(inbound.Id, line.InboundId);
        Assert.Equal(Start, line.At);
        Assert.Equal("tools/call", line.Method);
        Assert.Equal("echo", line.Tool);
        Assert.Equal(UsageOutcome.Ok, line.Outcome);
        Assert.Equal(new DailyTotal(Today, 1, 1, 0, 0, line.DurationMs), Assert.Single(await TotalsAsync(gateway)));
    }

    [Fact]
    public async Task Every_message_of_a_batch_gets_a_line_and_what_carries_no_method_gets_none()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAtAsync(new FakeTimeProvider(Start));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var listen = new HttpRequestMessage(HttpMethod.Get, "/mcp/echo");
        listen.Headers.Authorization = new("Bearer", issued.Token);
        using var stream = await gateway.Http.SendAsync(listen);
        using var close = new HttpRequestMessage(HttpMethod.Delete, "/mcp/echo");
        close.Headers.Authorization = new("Bearer", issued.Token);
        using var closed = await gateway.Http.SendAsync(close);
        using var answer = await gateway.Http.SendAsync(
            GatewayHost.Post("/mcp/echo", issued.Token, """{"jsonrpc":"2.0","id":5,"result":{}}"""));
        using var batch = await gateway.Http.SendAsync(GatewayHost.Post(
            "/mcp/echo",
            issued.Token,
            $$"""[{{ToolCall}},{"jsonrpc":"2.0","method":"notifications/progress"},{{Ping}}]"""));
        await WaitForLinesAsync(gateway, 3);

        var lines = await LinesAsync(gateway);
        Assert.Equal(["ping", "notifications/progress", "tools/call"], lines.Select(line => line.Method));
        Assert.Equal([null, null, "echo"], lines.Select(line => line.Tool));
        Assert.Equal(new DailyTotal(Today, 3, 1, 0, 0, lines.Sum(line => line.DurationMs)), Assert.Single(await TotalsAsync(gateway)));
    }

    [Fact]
    public async Task Quota_refusals_and_upstream_trouble_are_told_apart()
    {
        await using var upstream = await FakeUpstream.StartAsync((app, _) =>
        {
            app.Map("/broken", context =>
            {
                context.Response.StatusCode = 500;
                return Task.CompletedTask;
            });
            app.Map("/gone", context =>
            {
                context.Response.StatusCode = 404;
                return Task.CompletedTask;
            });
        });
        await using var gateway = await StartAtAsync(new FakeTimeProvider(Start));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("broken", upstream.BaseUrl + "/broken");
        await gateway.AddInboundAsync("gone", upstream.BaseUrl + "/gone");
        await gateway.AddInboundAsync("dead", "http://127.0.0.1:1/mcp");
        var issued = await gateway.AddClientAsync(
            "Иванов", ["echo", "broken", "gone", "dead"], limits: new ClientLimits(QuotaCalls: 1));

        foreach (var slug in new[] { "broken", "gone", "dead", "echo", "echo" })
        {
            using var response = await gateway.Http.SendAsync(GatewayHost.Post($"/mcp/{slug}", issued.Token, ToolCall));
        }

        await WaitForLinesAsync(gateway, 5);

        Assert.Equal(
            [
                UsageOutcome.QuotaExhausted,
                UsageOutcome.Ok,
                UsageOutcome.UpstreamFailed,
                UsageOutcome.UpstreamRejected,
                UsageOutcome.UpstreamFailed,
            ],
            (await LinesAsync(gateway)).Select(line => line.Outcome));
        var total = Assert.Single(await TotalsAsync(gateway));
        Assert.Equal((4, 4, 2, 1), (total.Calls, total.ToolCalls, total.Errors, total.Refused));
    }

    [Fact]
    public async Task Refusals_made_before_the_method_is_known_are_counted_without_a_line()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAtAsync(new FakeTimeProvider(Start));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var hasty = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 1));
        var sloppy = await gateway.AddClientAsync("Петров", ["echo"]);

        using var served = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", hasty.Token, Ping));
        using var limited = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", hasty.Token, Ping));
        using var limitedStream = new HttpRequestMessage(HttpMethod.Get, "/mcp/echo");
        limitedStream.Headers.Authorization = new("Bearer", hasty.Token);
        using var limitedToo = await gateway.Http.SendAsync(limitedStream);
        using var unreadable = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", sloppy.Token, "не json"));

        await Eventually.TrueAsync(
            async () => (await TotalsAsync(gateway)).Sum(total => total.Refused) == 3,
            "отказы посчитаны");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedToo.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
        Assert.Equal("ping", Assert.Single(await LinesAsync(gateway)).Method);
        var hastyTotal = Assert.Single(await TotalsAsync(gateway, hasty.Client.Id));
        var sloppyTotal = Assert.Single(await TotalsAsync(gateway, sloppy.Client.Id));
        Assert.Equal((1, 2), (hastyTotal.Calls, hastyTotal.Refused));
        Assert.Equal((0, 1), (sloppyTotal.Calls, sloppyTotal.Refused));
    }

    [Fact]
    public async Task Lines_older_than_the_retention_period_are_removed_and_daily_totals_stay()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Configuration["CtxUi:UsageRetentionDays"] = "7";
        });
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await WaitForLinesAsync(gateway, 1);

        clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromHours(1));

        await Eventually.TrueAsync(async () => (await LinesAsync(gateway)).Count == 0, "старая строка удалена");
        Assert.Equal(1, Assert.Single(await TotalsAsync(gateway)).Calls);
    }

    [Fact]
    public async Task Shutdown_writes_what_was_still_in_memory()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(
            builder => builder.Configuration["CtxUi:FlushInterval"] = "01:00:00");
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        var beforeShutdown = await LinesAsync(gateway);

        await gateway.RestartAsync();

        Assert.Empty(beforeShutdown);
        Assert.Equal("echo", Assert.Single(await LinesAsync(gateway)).Tool);
        var client = await gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync("Иванов"));
        Assert.Equal(1, QuotaPeriod.UsedCalls(client, DateTimeOffset.UtcNow));
    }
}
