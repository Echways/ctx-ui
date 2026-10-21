using System.Net;

namespace CtxUi.IntegrationTests;

public class PanelAccessTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/logout")]
    [InlineData("/inbounds")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_content/CtxUi.Panel/panel.css")]
    [InlineData("/abcdefgh12345678/login")]
    public async Task Without_the_secret_path_the_panel_does_not_exist(string path)
    {
        await using var gateway = await GatewayHost.StartAsync();

        using var response = await gateway.Http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Secret_path_typed_without_the_slash_leads_to_the_one_with_it()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var response = await gateway.Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, panel.Root + "?from=bookmark"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(panel.Root + "/login", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/")]
    [InlineData("/mcp/echo/extra")]
    [InlineData("/mcp/echo/login")]
    public async Task Addresses_under_the_gateway_path_never_reach_the_panel(string path)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(gateway.BaseUrl) };

        using var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gateway_keeps_answering_beside_the_panel()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var byHeader = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var byPath = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{issued.Token}/mcp/echo"));
        using var noToken = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo"));

        Assert.Equal(HttpStatusCode.OK, byHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byPath.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }
}
