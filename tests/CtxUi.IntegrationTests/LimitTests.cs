using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Clients;

namespace CtxUi.IntegrationTests;

public class LimitTests
{
    private static async Task AssertLimitedAsync(HttpResponseMessage response, string error, string retryAfter)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(retryAfter, Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Contains($"\"error\":\"{error}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_over_the_rate_gets_429_with_a_wait_hint_and_stays_away_from_the_upstream()
    {
        var logs = new CapturingLogs();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 2));

        using var first = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var second = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{issued.Token}/mcp/echo"));
        using var third = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await AssertLimitedAsync(third, "rate_limited", "30");
        Assert.Contains("повторите через 30 с", await third.Content.ReadAsStringAsync());
        Assert.Equal(2, upstream.Requests.Count);
        Assert.Contains(logs.Lines, line => line.Contains("клиент=Иванов исход=RateLimited статус=429"));
        Assert.Contains(logs.Lines, line => line.Contains("клиент=Иванов исход=Ok статус=200"));
    }

    [Fact]
    public async Task Rate_counts_requests_of_every_http_method()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 2));

        HttpRequestMessage Request(HttpMethod method)
        {
            var request = new HttpRequestMessage(method, "/mcp/echo");
            request.Headers.Authorization = new("Bearer", issued.Token);
            return request;
        }

        using var stream = await gateway.Http.SendAsync(Request(HttpMethod.Get));
        using var close = await gateway.Http.SendAsync(Request(HttpMethod.Delete));
        using var message = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, message.StatusCode);
    }

    [Fact]
    public async Task Rate_of_one_client_does_not_hold_back_another()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var ivanov = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 1));
        var petrov = await gateway.AddClientAsync("Петров", ["echo"], limits: new ClientLimits(RatePerMinute: 1));
        using var spent = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", ivanov.Token));

        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", ivanov.Token));
        using var served = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", petrov.Token));

        await AssertLimitedAsync(refused, "rate_limited", "60");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task Changed_rate_applies_without_a_restart()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 1));
        using var spent = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await gateway.SetLimitsAsync("Иванов", ClientLimits.None);
        using var served = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task Request_over_the_concurrency_limit_gets_429_and_the_slot_frees_when_the_first_one_ends()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        await using var upstream = await FakeUpstream.StartAsync((app, _) => app.Map("/slow", async context =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Response.WriteAsync("done");
        }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("slow", upstream.BaseUrl + "/slow");
        var issued = await gateway.AddClientAsync("Иванов", ["slow"], limits: new ClientLimits(MaxConcurrency: 1));

        var inFlight = gateway.Http.SendAsync(GatewayHost.Post("/mcp/slow", issued.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/slow", issued.Token));
        release.SetResult();
        using var finished = await inFlight;
        using var next = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/slow", issued.Token));

        await AssertLimitedAsync(refused, "too_many_concurrent", "1");
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Open_event_stream_does_not_take_the_only_slot()
    {
        var release = new TaskCompletionSource();
        await using var upstream = await FakeUpstream.StartAsync((app, _) => app.Map("/session", async context =>
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await context.Response.WriteAsync("answer");
                return;
            }

            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(": listening\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("session", upstream.BaseUrl + "/session");
        var issued = await gateway.AddClientAsync("Иванов", ["session"], limits: new ClientLimits(MaxConcurrency: 1));

        using var listen = new HttpRequestMessage(HttpMethod.Get, "/mcp/session");
        listen.Headers.Authorization = new("Bearer", issued.Token);
        using var stream = await gateway.Http.SendAsync(listen, HttpCompletionOption.ResponseHeadersRead);
        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/session", issued.Token));
        release.SetResult();

        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Equal("answer", await call.Content.ReadAsStringAsync());
    }
}
