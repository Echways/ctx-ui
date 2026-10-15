using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;

namespace CtxUi.IntegrationTests;

public class AccessTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static async Task AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Contains($"\"error\":\"{error}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Request_without_a_token_gets_401_with_a_hint()
    {
        await using var gateway = await GatewayHost.StartAsync();

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo"));

        await AssertRejectedAsync(response, HttpStatusCode.Unauthorized, "token_missing");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains("нужен токен", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_token_gets_401()
    {
        await using var gateway = await GatewayHost.StartAsync();

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", AccessToken.Generate()));

        await AssertRejectedAsync(response, HttpStatusCode.Unauthorized, "token_unknown");
    }

    [Fact]
    public async Task Allowed_request_reaches_the_upstream_and_the_answer_comes_back()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов 4419", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, """{"jsonrpc":"2.0","id":1,"method":"ping"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/mcp", seen.Path);
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", seen.Body);
    }

    [Fact]
    public async Task Token_in_the_address_works_like_the_header()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{issued.Token}/mcp/echo"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/mcp", Assert.Single(upstream.Requests).Path);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Other_methods_are_forwarded_too(string method)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var request = new HttpRequestMessage(new HttpMethod(method), "/mcp/echo");
        request.Headers.Authorization = new("Bearer", issued.Token);
        using var response = await gateway.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(method, Assert.Single(upstream.Requests).Method);
    }

    [Fact]
    public async Task Disabled_client_gets_401_and_nothing_reaches_the_upstream()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().SetEnabledAsync("Иванов", false);
            return true;
        });

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await AssertRejectedAsync(response, HttpStatusCode.Unauthorized, "client_disabled");
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Expired_client_gets_401_with_the_expiry_date()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], Start.AddDays(1));

        using var before = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        clock.Advance(TimeSpan.FromDays(1));
        using var after = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await AssertRejectedAsync(after, HttpStatusCode.Unauthorized, "client_expired");
        Assert.Contains("2026-10-13", await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Inbound_the_client_has_no_access_to_looks_like_a_missing_one()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var foreign = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/docs", issued.Token));
        using var missing = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/nope", issued.Token));

        await AssertRejectedAsync(foreign, HttpStatusCode.NotFound, "inbound_not_found");
        await AssertRejectedAsync(missing, HttpStatusCode.NotFound, "inbound_not_found");
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Disabled_inbound_gets_404()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<InboundService>().SetEnabledAsync("echo", false);
            return true;
        });

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await AssertRejectedAsync(response, HttpStatusCode.NotFound, "inbound_not_found");
    }

    [Fact]
    public async Task Change_made_by_another_process_is_applied_without_a_restart()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        await gateway.ChangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().SetEnabledAsync("Иванов", false);
            return true;
        });

        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
                return response.StatusCode == HttpStatusCode.Unauthorized;
            },
            "отключённый клиент получает 401");
    }

    [Fact]
    public async Task Keys_directory_is_readable_by_the_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var gateway = await GatewayHost.StartAsync();

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(gateway.DataDir, "keys")));
    }

    [Fact]
    public async Task Existing_data_directory_is_closed_to_others()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dataDir = Directory.CreateTempSubdirectory("ctx-ui-").FullName;
        try
        {
            File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            await CliRunner.RunAsync(dataDir, "client", "list");

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dataDir));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Token_never_appears_in_the_log_whatever_way_it_arrives()
    {
        var logs = new CapturingLogs();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var stranger = AccessToken.Generate();

        using var byPath = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{issued.Token}/mcp/echo?x=1"));
        using var byHeader = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var unknown = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{stranger}/mcp/echo"));
        using var noRoute = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{issued.Token}/elsewhere"));

        Assert.Contains(logs.Lines, line => line.Contains("/mcp/echo"));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(issued.Token));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(stranger));
    }
}
