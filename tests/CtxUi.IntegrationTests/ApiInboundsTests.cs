using System.Net;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Inbounds;

namespace CtxUi.IntegrationTests;

public class ApiInboundsTests
{
    private static Task<GatewayHost> StartWithoutPollingAsync() =>
        GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");

    [Fact]
    public async Task Put_creates_an_inbound_and_answers_with_its_address()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.PutAsync("api/v1/inbounds/learn", new
        {
            url = "https://learn.example.org/api/mcp",
            name = "Учебный",
            upstreamHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" },
        });
        var body = await ApiClient.ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/{gateway.PanelPath}/api/v1/inbounds/learn", response.Headers.Location?.OriginalString);
        Assert.Equal("learn", body.GetProperty("slug").GetString());
        Assert.Equal("Учебный", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(Inbound.DefaultRequestTimeoutSeconds, body.GetProperty("requestTimeoutSeconds").GetInt32());
        Assert.Equal("Authorization", body.GetProperty("upstreamHeaderNames")[0].GetString());
        Assert.DoesNotContain("s3cret", body.GetRawText());
    }

    [Fact]
    public async Task Put_on_an_existing_inbound_replaces_it_and_keeps_headers_that_were_not_sent()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("learn", "https://old.example.org/mcp", [KeyValuePair.Create("X-Key", "v")]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.PutAsync("api/v1/inbounds/learn", new { url = "https://new.example.org/mcp", requestTimeoutSeconds = 30 });
        var body = await ApiClient.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://new.example.org/mcp", body.GetProperty("url").GetString());
        Assert.Equal(30, body.GetProperty("requestTimeoutSeconds").GetInt32());
        Assert.Equal("X-Key", body.GetProperty("upstreamHeaderNames")[0].GetString());
    }

    [Fact]
    public async Task Empty_header_object_removes_all_headers()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("learn", "https://old.example.org/mcp", [KeyValuePair.Create("X-Key", "v")]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.PutAsync(
            "api/v1/inbounds/learn", new { url = "https://old.example.org/mcp", upstreamHeaders = new Dictionary<string, string>() });

        Assert.Equal(0, (await ApiClient.ReadAsync(response)).GetProperty("upstreamHeaderNames").GetArrayLength());
    }

    [Theory]
    [InlineData("Learn", "https://learn.example.org/mcp", "Slug «Learn» недопустим")]
    [InlineData("learn", "ftp://learn.example.org/mcp", "Адрес «ftp://learn.example.org/mcp» недопустим")]
    [InlineData("learn", null, "Адрес «» недопустим")]
    public async Task Invalid_inbound_is_refused_with_the_same_words_as_the_cli(string slug, string? url, string expected)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.PutAsync($"api/v1/inbounds/{slug}", new { url });
        var problem = await ApiClient.ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(expected, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Inbounds_are_listed_with_client_counts()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddInboundAsync("learn", "https://learn.example.org/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = await ApiClient.IssueAsync(gateway);

        var list = await ApiClient.ReadAsync(await api.GetAsync("api/v1/inbounds"));

        Assert.Equal(["echo", "learn"], list.EnumerateArray().Select(inbound => inbound.GetProperty("slug").GetString()));
        Assert.Equal([1, 0], list.EnumerateArray().Select(inbound => inbound.GetProperty("clients").GetInt32()));
    }

    [Fact]
    public async Task Unknown_inbound_is_404_with_its_name()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var get = await api.GetAsync("api/v1/inbounds/nope");
        using var remove = await api.DeleteAsync("api/v1/inbounds/nope");
        using var enable = await api.PostAsync("api/v1/inbounds/nope/enable");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, enable.StatusCode);
        Assert.Equal("Inbound «nope» не найден", (await ApiClient.ReadAsync(get)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Changes_reach_the_gateway_without_waiting_for_the_refresh()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var client = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var disable = await api.PostAsync("api/v1/inbounds/echo/disable");
        using var whileDisabled = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", client.Token));
        using var enable = await api.PostAsync("api/v1/inbounds/echo/enable");
        using var whileEnabled = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", client.Token));
        using var remove = await api.DeleteAsync("api/v1/inbounds/echo");
        using var afterRemoval = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", client.Token));

        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, whileDisabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, whileEnabled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterRemoval.StatusCode);
        Assert.Empty(await gateway.ChangeAsync(services => services.GetRequiredService<InboundService>().ListAsync()));
    }
}
