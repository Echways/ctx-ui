using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CtxUi.IntegrationTests;

public class ForwardingTests
{
    private static KeyValuePair<string, string>[] Pairs(params (string Key, string Value)[] items) =>
        items.Select(item => KeyValuePair.Create(item.Key, item.Value)).ToArray();

    [Fact]
    public async Task Client_token_is_replaced_by_the_inbound_headers()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl, Pairs(("Authorization", "Bearer upstream-key"), ("X-Api-Key", "k1")));
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("Bearer upstream-key", seen.Headers["Authorization"]);
        Assert.Equal("k1", seen.Headers["X-Api-Key"]);
        Assert.DoesNotContain(seen.Headers.Values, value => value.Contains(issued.Token));
    }

    [Fact]
    public async Task Without_inbound_headers_the_upstream_sees_no_authorization()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.False(Assert.Single(upstream.Requests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Identity_reaches_the_upstream_intact_even_in_cyrillic()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl, forwardIdentity: true);
        var issued = await gateway.AddClientAsync(
            "Иванов 4419", ["echo"], metadata: Pairs(("storage-mb", "50"), ("group", "ИКТ 4419")));

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        var seen = Assert.Single(upstream.Requests);
        Assert.Equal(issued.Client.Id.ToString(), seen.Headers["X-Mcp-Client-Id"]);
        Assert.Equal("Иванов 4419", Uri.UnescapeDataString(seen.Headers["X-Mcp-Client-Name"]));
        Assert.Equal("50", seen.Headers["X-Mcp-Meta-storage-mb"]);
        Assert.Equal("ИКТ 4419", Uri.UnescapeDataString(seen.Headers["X-Mcp-Meta-group"]));
    }

    [Fact]
    public async Task Identity_is_not_sent_unless_the_inbound_asks_for_it()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], metadata: Pairs(("group", "4419")));

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.DoesNotContain(Assert.Single(upstream.Requests).Headers.Keys, name => name.StartsWith("X-Mcp-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_headers_sent_by_the_client_never_reach_the_upstream(bool forwardIdentity)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl, forwardIdentity: forwardIdentity);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var request = GatewayHost.Post("/mcp/echo", issued.Token);
        request.Headers.TryAddWithoutValidation("X-Mcp-Client-Id", "spoofed");
        request.Headers.TryAddWithoutValidation("x-mcp-client-name", "root");
        request.Headers.TryAddWithoutValidation("X-Mcp-Meta-admin", "true");
        using var response = await gateway.Http.SendAsync(request);

        var seen = Assert.Single(upstream.Requests);
        Assert.False(seen.Headers.ContainsKey("X-Mcp-Meta-admin"));
        Assert.DoesNotContain(seen.Headers.Values, value => value is "spoofed" or "root");
        Assert.Equal(forwardIdentity, seen.Headers.ContainsKey("X-Mcp-Client-Id"));
    }

    [Fact]
    public async Task Panel_cookies_never_reach_the_upstream_and_the_rest_do()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var request = GatewayHost.Post("/mcp/echo", issued.Token);
        request.Headers.TryAddWithoutValidation(
            "Cookie", "ctx-ui=s; ctx-uiC1=p; .AspNetCore.Culture=c=ru|uic=ru; lb=7; .AspNetCore.Antiforgery.a=x; theme=dark");
        using var response = await gateway.Http.SendAsync(request);

        Assert.Equal("lb=7; theme=dark", Assert.Single(upstream.Requests).Headers["Cookie"]);
    }

    [Fact]
    public async Task Upstream_cookies_reach_it_exactly_as_the_client_sent_them()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var request = GatewayHost.Post("/mcp/echo", issued.Token);
        request.Headers.TryAddWithoutValidation("Cookie", "note=a b; ctx-ui=s; prefs={\"x\":1,\"y\":2}; lb=7");
        using var response = await gateway.Http.SendAsync(request);

        Assert.Equal("note=a b; prefs={\"x\":1,\"y\":2}; lb=7", Assert.Single(upstream.Requests).Headers["Cookie"]);
    }

    [Fact]
    public async Task Request_with_only_panel_cookies_reaches_the_upstream_without_a_cookie_header()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var request = GatewayHost.Post("/mcp/echo", issued.Token);
        request.Headers.TryAddWithoutValidation("Cookie", "ctx-ui=s; .AspNetCore.Culture=c=ru|uic=ru");
        using var response = await gateway.Http.SendAsync(request);

        Assert.False(Assert.Single(upstream.Requests).Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task Upstream_address_is_used_as_configured_and_the_client_query_is_dropped()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl + "?tenant=7");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo?evil=1", issued.Token));

        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("/mcp", seen.Path);
        Assert.Equal("?tenant=7", seen.Query);
    }

    [Fact]
    public async Task Protocol_headers_pass_in_both_directions()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var request = GatewayHost.Post("/mcp/echo", issued.Token);
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", "abc");
        request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2025-11-25");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var response = await gateway.Http.SendAsync(request);

        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("abc", seen.Headers["Mcp-Session-Id"]);
        Assert.Equal("2025-11-25", seen.Headers["Mcp-Protocol-Version"]);
        Assert.Equal("application/json, text/event-stream", seen.Headers["Accept"]);
        Assert.Equal("session-1", Assert.Single(response.Headers.GetValues("Mcp-Session-Id")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Upstream_status_and_body_are_returned_as_is()
    {
        await using var upstream = await FakeUpstream.StartAsync((app, _) => app.Map("/teapot", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status418ImATeapot;
            await context.Response.WriteAsync("no coffee");
        }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("tea", upstream.BaseUrl + "/teapot");
        var issued = await gateway.AddClientAsync("Иванов", ["tea"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/tea", issued.Token));

        Assert.Equal((HttpStatusCode)418, response.StatusCode);
        Assert.Equal("no coffee", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Event_stream_is_delivered_as_it_is_produced()
    {
        var release = new TaskCompletionSource();
        await using var upstream = await FakeUpstream.StartAsync((app, _) => app.Map("/stream", async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: one\n\n");
            await context.Response.Body.FlushAsync();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Response.WriteAsync("data: two\n\n");
        }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("stream", upstream.BaseUrl + "/stream");
        var issued = await gateway.AddClientAsync("Иванов", ["stream"]);

        using var response = await gateway.Http.SendAsync(
            GatewayHost.Post("/mcp/stream", issued.Token), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        var rest = await reader.ReadToEndAsync();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("data: one", first);
        Assert.Contains("data: two", rest);
    }

    [Fact]
    public async Task Unreachable_upstream_gives_502()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("dead", "http://127.0.0.1:1/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["dead"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/dead", issued.Token));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_call_to_the_upstream()
    {
        var started = new TaskCompletionSource();
        var aborted = new TaskCompletionSource();
        await using var upstream = await FakeUpstream.StartAsync((app, _) => app.Map("/slow", async context =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                aborted.TrySetResult();
            }
        }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("slow", upstream.BaseUrl + "/slow");
        var issued = await gateway.AddClientAsync("Иванов", ["slow"]);

        using var cancellation = new CancellationTokenSource();
        var call = gateway.Http.SendAsync(GatewayHost.Post("/mcp/slow", issued.Token), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task Large_body_arrives_whole()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var body = "{\"data\":\"" + new string('x', 1024 * 1024) + "\"}";

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.Length, Assert.Single(upstream.Requests).Body.Length);
    }
}
