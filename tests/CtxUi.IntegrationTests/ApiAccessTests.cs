using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.ApiKeys;

namespace CtxUi.IntegrationTests;

public class ApiAccessTests
{
    [Theory]
    [InlineData("/api/v1/inbounds")]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi/v1.json")]
    public async Task Without_the_secret_path_the_api_does_not_exist(string path)
    {
        await using var gateway = await GatewayHost.StartAsync();
        var key = (await gateway.ChangeAsync(services => services.GetRequiredService<ApiKeyService>().AddAsync("bot"))).Secret;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", key);

        using var response = await gateway.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ctxk_0000000000000000000000000000000000000000")]
    [InlineData("not-a-key")]
    public async Task Request_without_a_valid_key_is_refused_with_a_bearer_challenge(string? key)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = ApiClient.WithKey(gateway, key);

        using var response = await api.GetAsync("api/v1/inbounds");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Client_token_is_not_an_api_key()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        var client = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = ApiClient.WithKey(gateway, client.Token);

        using var response = await api.GetAsync("api/v1/inbounds");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signed_in_panel_session_does_not_open_the_api()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);
        using var signedIn = await panel.SignInAsync(admin.Admin.Name, admin.Password);

        using var response = await panel.GetAsync("api/v1/inbounds");

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_stops_working_at_once()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway, "bot");
        using var before = await api.GetAsync("api/v1/inbounds");

        await gateway.ChangeAsync(async services =>
        {
            await services.GetRequiredService<ApiKeyService>().RemoveAsync("bot");
            return true;
        });
        using var after = await api.GetAsync("api/v1/inbounds");

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData("api/v1/nope")]
    [InlineData("api/v2/inbounds")]
    [InlineData("api/v1/clients/not-a-guid")]
    public async Task Unknown_api_address_is_404_not_the_panel_login(string path)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Broken_json_gets_a_problem_answer()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.SendRawAsync(HttpMethod.Put, "api/v1/inbounds/echo", """{"url":""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Swagger_ui_is_served_under_the_secret_path_without_a_key()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = ApiClient.WithKey(gateway, null);

        using var redirect = await api.GetAsync("swagger");
        using var page = await api.GetAsync("swagger/index.html");
        using var settings = await api.GetAsync("swagger/index.js");

        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("swagger/index.html", redirect.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>ctx-ui API</title>", await page.Content.ReadAsStringAsync());
        Assert.Contains("\"url\":\"../openapi/v1.json\"", await settings.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Openapi_document_describes_the_api_behind_the_proxy()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "openapi/v1.json");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Host = "mcp.example.org";

        using var http = new HttpClient { BaseAddress = new Uri(gateway.PanelUrl) };
        using var response = await http.SendAsync(request);
        var document = await ApiClient.ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("3.1", document.GetProperty("openapi").GetString());
        Assert.Equal($"https://mcp.example.org/{gateway.PanelPath}", document.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.Equal("bearer", document.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey").GetProperty("scheme").GetString());
        var paths = document.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToList();
        Assert.Contains("/api/v1/inbounds/{slug}", paths);
        Assert.All(paths, path => Assert.StartsWith("/api/v1/", path));
        Assert.DoesNotContain(paths, path => path.Contains("rest", StringComparison.Ordinal));
    }
}
