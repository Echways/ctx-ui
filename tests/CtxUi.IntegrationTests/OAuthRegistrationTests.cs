using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Connect.Registration;
using CtxUi.Core.Inbounds;

namespace CtxUi.IntegrationTests;

public class OAuthRegistrationTests
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string Callback = "http://127.0.0.1/callback";

    private static Task<GatewayHost> StartAsync(string? publicUrlSuffix = null) => StartAsync(_ => { }, publicUrlSuffix);

    private static Task<GatewayHost> StartAsync(Action<WebApplicationBuilder> configure, string? publicUrlSuffix = null) =>
        OAuthHarness.StartAsync(
            builder =>
            {
                builder.Services.AddSingleton<IMetadataAddressPolicy, LoopbackMetadataPolicy>();
                configure(builder);
            },
            publicUrlSuffix);

    private static HttpClient Browser(GatewayHost gateway) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(gateway.BaseUrl) };

    private static string Authorize(string clientId, string redirectUri, string? resource, string scope = "mcp offline_access")
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = "s1",
            ["code_challenge"] = Challenge,
            ["code_challenge_method"] = "S256",
        };
        if (resource is not null)
            query["resource"] = resource;

        return QueryHelpers.AddQueryString("/connect/authorize", query);
    }

    private static string? RedirectError(HttpResponseMessage response, string redirectUri)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(redirectUri, location);
        return QueryHelpers.ParseQuery(new Uri(location).Query)["error"];
    }

    private static async Task<string> RegisterAsync(GatewayHost gateway, string name, params string[] redirectUris)
    {
        using var response = await gateway.Http.PostAsJsonAsync("/connect/register", new Dictionary<string, object>
        {
            ["client_name"] = name,
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    [Fact]
    public async Task Cimd_client_reaches_the_sign_in_page()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For("http://localhost/callback");
        using var browser = Browser(gateway);

        using var first = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:3118/callback", gateway.Issuer + "/mcp/echo"));
        using var second = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:4000/callback", gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.StartsWith("/connect/signin?returnUrl=", first.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.Equal(1, metadata.Fetches);
    }

    [Fact]
    public async Task Cimd_document_with_a_foreign_redirect_is_refused()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For("https://claude.ai/api/mcp/auth_callback");
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, "https://evil.example.org/callback", gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("redirect_uri", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cimd_document_that_names_another_client_is_refused()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.Document = """{"client_id":"https://claude.ai/other","client_name":"Claude","redirect_uris":["http://localhost/callback"]}""";
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:3118/callback", gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_client", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Production_policy_does_not_fetch_documents_from_this_machine()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For("http://localhost/callback");
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:3118/callback", gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("client_id", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, metadata.Fetches);
    }

    [Fact]
    public async Task Dcr_registration_with_the_same_metadata_returns_the_same_client()
    {
        await using var gateway = await StartAsync();

        var first = await RegisterAsync(gateway, "Cursor", "cursor://anysphere.cursor-mcp/oauth/callback", Callback);
        var again = await RegisterAsync(gateway, "Cursor", Callback, "cursor://anysphere.cursor-mcp/oauth/callback");
        var other = await RegisterAsync(gateway, "Cursor", Callback);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task Dcr_answers_with_the_registered_metadata()
    {
        await using var gateway = await StartAsync();

        using var response = await gateway.Http.PostAsJsonAsync(
            "/connect/register",
            new Dictionary<string, object> { ["client_name"] = "Claude Code", ["redirect_uris"] = new[] { "http://localhost:3118/callback" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Claude Code", json.GetProperty("client_name").GetString());
        Assert.Equal("none", json.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal(["http://localhost:3118/callback"], json.GetProperty("redirect_uris").EnumerateArray().Select(uri => uri.GetString()));
        Assert.Equal(["authorization_code", "refresh_token"], json.GetProperty("grant_types").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal(["code"], json.GetProperty("response_types").EnumerateArray().Select(type => type.GetString()));
        Assert.False(json.TryGetProperty("client_secret", out _));
    }

    [Theory]
    [InlineData("""{"client_name":"Bad","redirect_uris":["http://localhost/cb"],"token_endpoint_auth_method":"client_secret_basic"}""", "invalid_client_metadata")]
    [InlineData("""{"client_name":"Bad","redirect_uris":["http://localhost/cb"],"grant_types":["client_credentials"]}""", "invalid_client_metadata")]
    [InlineData("""{"client_name":"Bad","redirect_uris":["http://localhost/cb"],"response_types":["token"]}""", "invalid_client_metadata")]
    [InlineData("""{"client_name":"Bad","redirect_uris":[]}""", "invalid_redirect_uri")]
    [InlineData("""{"client_name":"Bad","redirect_uris":["http://example.org/cb"]}""", "invalid_redirect_uri")]
    [InlineData("""{"client_name":"Bad","redirect_uris":["javascript:alert(1)"]}""", "invalid_redirect_uri")]
    [InlineData("""not json""", "invalid_client_metadata")]
    public async Task Dcr_refuses_confidential_clients(string body, string error)
    {
        await using var gateway = await StartAsync();

        using var response = await gateway.Http.PostAsync(
            "/connect/register", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Resource_matches_ignoring_scheme_and_host_case_and_trailing_slash_of_public_url()
    {
        await using var gateway = await StartAsync(publicUrlSuffix: "/");
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var clientId = await RegisterAsync(gateway, "Claude Code", Callback);
        using var browser = Browser(gateway);
        var resource = "HTTP://127.0.0.1" + gateway.Issuer["http://127.0.0.1".Length..] + "/mcp/echo/";

        using var response = await browser.GetAsync(Authorize(clientId, Callback, resource));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/connect/signin?returnUrl=", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Unknown_or_disabled_inbound_resource_is_invalid_target()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        await gateway.AddInboundAsync("docs", "https://mcp.example.org/mcp");
        await gateway.ArrangeAsync(services => services.GetRequiredService<InboundService>().SetEnabledAsync("docs", false));
        var clientId = await RegisterAsync(gateway, "Claude Code", Callback);
        using var browser = Browser(gateway);

        using var unknown = await browser.GetAsync(Authorize(clientId, Callback, gateway.Issuer + "/mcp/nope"));
        using var disabled = await browser.GetAsync(Authorize(clientId, Callback, gateway.Issuer + "/mcp/docs"));
        using var foreign = await browser.GetAsync(Authorize(clientId, Callback, "https://other.example.org/mcp/echo"));

        Assert.Equal("invalid_target", RedirectError(unknown, Callback));
        Assert.Equal("invalid_target", RedirectError(disabled, Callback));
        Assert.Equal("invalid_target", RedirectError(foreign, Callback));
    }

    [Fact]
    public async Task Missing_resource_is_invalid_target()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        await gateway.AddInboundAsync("docs", "https://mcp.example.org/mcp");
        var clientId = await RegisterAsync(gateway, "Claude Code", Callback);
        using var browser = Browser(gateway);

        using var missing = await browser.GetAsync(Authorize(clientId, Callback, null));
        using var twice = await browser.GetAsync(Authorize(clientId, Callback, gateway.Issuer + "/mcp/echo") + "&resource=" + Uri.EscapeDataString(gateway.Issuer + "/mcp/docs"));

        Assert.Equal("invalid_target", RedirectError(missing, Callback));
        Assert.Equal("invalid_target", RedirectError(twice, Callback));
    }

    [Fact]
    public async Task Token_request_without_resource_is_invalid_target()
    {
        await using var gateway = await StartAsync();
        var clientId = await RegisterAsync(gateway, "Claude Code", Callback);

        using var response = await gateway.Http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = "not-a-code",
            ["redirect_uri"] = Callback,
            ["code_verifier"] = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_target", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task App_identity_groups_registrations_by_name_and_redirects()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        var dcr = await RegisterAsync(gateway, "Cursor", "cursor://anysphere.cursor-mcp/oauth/callback", Callback);
        metadata.For("http://localhost/callback");
        var cimdKnown = await gateway.ChangeAsync(services => services.GetRequiredService<RegisteredApps>().EnsureFromDocumentAsync(metadata.Url, default));

        var dynamic = await gateway.ChangeAsync(services => services.GetRequiredService<RegisteredApps>().IdentityAsync(dcr, default));
        var document = await gateway.ChangeAsync(services => services.GetRequiredService<RegisteredApps>().IdentityAsync(metadata.Url, default));

        Assert.True(cimdKnown);
        Assert.StartsWith(AppIdentity.DynamicPrefix, dynamic!.Key);
        Assert.Equal("Cursor", dynamic.Name);
        Assert.Equivalent(new[] { "cursor://anysphere.cursor-mcp", "127.0.0.1" }, dynamic.RedirectHosts);
        Assert.False(dynamic.LoopbackOnly);
        Assert.Equal(metadata.Url, document!.Key);
        Assert.Equal("Claude Code", document.Name);
        Assert.True(document.LoopbackOnly);
        Assert.Null(await gateway.ChangeAsync(services => services.GetRequiredService<RegisteredApps>().IdentityAsync("nobody", default)));
    }

    [Fact]
    public async Task Unused_dynamic_registrations_are_pruned_after_thirty_days()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        await using var gateway = await StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        var old = await RegisterAsync(gateway, "Cursor", Callback);
        clock.Advance(TimeSpan.FromDays(20));
        var fresh = await RegisterAsync(gateway, "Claude Code", Callback);
        clock.Advance(TimeSpan.FromDays(11));

        var removed = await gateway.ChangeAsync(services =>
            services.GetRequiredService<RegisteredApps>().PruneAsync(clock.GetUtcNow().AddDays(-30), default));

        Assert.Equal(1, removed);
        var apps = await gateway.ChangeAsync(async services =>
            (await services.GetRequiredService<RegisteredApps>().IdentityAsync(old, default),
             await services.GetRequiredService<RegisteredApps>().IdentityAsync(fresh, default)));
        Assert.Null(apps.Item1);
        Assert.NotNull(apps.Item2);
    }
}
