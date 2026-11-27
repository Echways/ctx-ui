using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenIddict.Abstractions;
using CtxUi.Connect.Registration;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Settings;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.IntegrationTests;

public class OAuthRegistrationTests
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string Callback = "http://127.0.0.1/callback";

    private static Task<GatewayHost> StartAsync(string? publicUrlSuffix = null)
    {
        return StartAsync(_ => { }, publicUrlSuffix);
    }

    private static Task<GatewayHost> StartAsync(Action<WebApplicationBuilder> configure, string? publicUrlSuffix = null)
    {
        return OAuthHarness.StartAsync(
            builder =>
            {
                builder.Services.AddSingleton<IMetadataAddressPolicy, LoopbackMetadataPolicy>();
                configure(builder);
            },
            publicUrlSuffix);
    }

    private static HttpClient Browser(GatewayHost gateway)
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(gateway.BaseUrl) };
    }

    private static string Authorize(string clientId, string redirectUri, string? resource,
        string scope = "mcp offline_access", string? challengeMethod = "S256")
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = "s1",
            ["code_challenge"] = challengeMethod is null ? null : Challenge,
            ["code_challenge_method"] = challengeMethod
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
            ["response_types"] = new[] { "code" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    [Fact]
    public async Task Cimd_callback_specific_redirect_is_validated_for_a_harness_resource()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        const string origin = "https://tunnel.example.org";
        await gateway.ArrangeAsync(services => services.GetRequiredService<PublicEndpointSettings>()
            .SaveProfileAsync(new HarnessProfile("chatgpt-web", "ChatGPT Web", true, origin)));
        const string redirect = "https://chatgpt.com/connector/oauth/synthetic-callback-id";
        metadata.For(redirect);
        using var browser = Browser(gateway);

        using var accepted = await browser.GetAsync(Authorize(metadata.Url, redirect, origin + "/mcp/echo"));
        Assert.Equal(HttpStatusCode.Found, accepted.StatusCode);
        Assert.StartsWith("/connect/signin?returnUrl=", accepted.Headers.Location!.OriginalString);
        using var rejected = await browser.GetAsync(Authorize(metadata.Url, redirect + "-other", origin + "/mcp/echo"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Null(rejected.Headers.Location);
    }

    [Fact]
    public async Task Cimd_client_reaches_the_sign_in_page()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For("http://localhost/callback");
        using var browser = Browser(gateway);

        using var first = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:3118/callback",
            gateway.Issuer + "/mcp/echo"));
        using var second = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:4000/callback",
            gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.StartsWith("/connect/signin?returnUrl=", first.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.Equal(1, metadata.Fetches);
    }

    [Fact]
    public async Task Cimd_public_choice_works_through_token_refresh_revocation_and_mcp()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("other", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Test", ["echo", "other"]);
        metadata.Document = $$"""
            {
              "client_id": "{{metadata.Url}}",
              "client_name": "Metadata client",
              "redirect_uris": ["{{Callback}}"],
              "token_endpoint_auth_method": "private_key_jwt",
              "token_endpoint_auth_methods_supported": ["private_key_jwt", "none"],
              "jwks_uri": "https://keys.example.org/jwks.json",
              "token_endpoint_auth_signing_alg": "RS256",
              "grant_types": ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:jwt-bearer"],
              "response_types": ["code"]
            }
            """;

        using var flow = await OAuthFlow.StartAsync(gateway, "echo", clientId: metadata.Url, scope: "mcp");
        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        Assert.NotNull(flow.Code);

        await gateway.ArrangeAsync(async services =>
        {
            var apps = services.GetRequiredService<IOpenIddictApplicationManager>();
            var app = await apps.FindByClientIdAsync(metadata.Url);
            Assert.NotNull(app);
            Assert.Equal(ClientTypes.Public, await apps.GetClientTypeAsync(app));
            Assert.Equivalent(new[]
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code, Permissions.Prefixes.Scope + "mcp",
                Permissions.Prefixes.Scope + Scopes.OfflineAccess
            }, await apps.GetPermissionsAsync(app), strict: true);
            Assert.Contains(Requirements.Features.ProofKeyForCodeExchange, await apps.GetRequirementsAsync(app));
        });

        var exchange = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode,
            ["code"] = flow.Code,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = new string('a', 43)
        };
        Assert.Equal(Errors.InvalidGrant, (await flow.TokenAsync(exchange)).Error);
        exchange["code_verifier"] = flow.Verifier;
        exchange["resource"] = gateway.Issuer + "/mcp/other";
        Assert.Equal(Errors.InvalidTarget, (await flow.TokenAsync(exchange)).Error);
        exchange.Remove("resource");
        var (access, refresh) = await flow.ExchangeAsync();
        using var call = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", access));
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        using var other = await gateway.Http.SendAsync(OAuthFlow.Mcp("other", access));
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

        var wrongResource = await flow.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.RefreshToken,
            ["refresh_token"] = refresh,
            ["resource"] = gateway.Issuer + "/mcp/other"
        });
        Assert.Equal(Errors.InvalidTarget, wrongResource.Error);
        var rotated = await flow.RefreshAsync(refresh);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.NotNull(rotated.Refresh);
        Assert.NotEqual(refresh, rotated.Refresh);
        using var revocation = await gateway.Http.PostAsync("/connect/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["client_id"] = metadata.Url, ["token"] = rotated.Refresh }));
        Assert.Equal(HttpStatusCode.OK, revocation.StatusCode);
        Assert.Equal(Errors.InvalidGrant, (await flow.RefreshAsync(rotated.Refresh)).Error);

        Assert.Equal(Errors.InvalidGrant, (await flow.TokenAsync(exchange)).Error);
        Assert.Equal(1, metadata.Fetches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("plain")]
    public async Task Cimd_client_requires_s256(string? challengeMethod)
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For(Callback);
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, Callback, gateway.Issuer + "/mcp/echo",
            challengeMethod: challengeMethod));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains(Errors.InvalidRequest, page);
        Assert.Contains("code_challenge", page);
    }

    [Fact]
    public async Task Cimd_refresh_revalidates_authentication_methods_after_cache_expiry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.Document = $$"""
            {
              "client_id": "{{metadata.Url}}", "client_name": "Metadata client", "redirect_uris": ["{{Callback}}"],
              "token_endpoint_auth_methods_supported": ["private_key_jwt"],
              "jwks_uri": "https://keys.example.org/jwks.json"
            }
            """;
        var confidential = metadata.Document;
        using var browser = Browser(gateway);
        var authorize = Authorize(metadata.Url, Callback, gateway.Issuer + "/mcp/echo");
        using var initial = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.BadRequest, initial.StatusCode);
        Assert.Contains(Errors.InvalidClient, await initial.Content.ReadAsStringAsync());

        metadata.For(Callback);
        using var accepted = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Found, accepted.StatusCode);
        metadata.Document = confidential;
        using var cached = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Found, cached.StatusCode);
        Assert.Equal(2, metadata.Fetches);

        clock.Advance(TimeSpan.FromMinutes(11));
        using var expired = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Contains(Errors.InvalidClient, await expired.Content.ReadAsStringAsync());
        Assert.Equal(3, metadata.Fetches);

        metadata.For("https://client.example/new-callback");
        using var previous = await browser.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.BadRequest, previous.StatusCode);
        Assert.Contains("redirect_uri", await previous.Content.ReadAsStringAsync());
        Assert.Equal(4, metadata.Fetches);
    }

    [Fact]
    public async Task Cimd_client_that_only_declares_authorization_code_gets_no_refresh_token()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Test", ["echo"]);
        metadata.Document = $$"""
            {
              "client_id": "{{metadata.Url}}", "client_name": "Metadata client", "redirect_uris": ["{{Callback}}"],
              "grant_types": ["authorization_code"], "response_types": ["code"]
            }
            """;

        using var flow = await OAuthFlow.StartAsync(gateway, "echo", clientId: metadata.Url, scope: "mcp");
        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        Assert.NotNull(flow.Code);

        var answer = await flow.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.AuthorizationCode,
            ["code"] = flow.Code,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = flow.Verifier
        });
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.NotNull(answer.Access);
        Assert.Null(answer.Refresh);
    }

    [Fact]
    public async Task Cimd_document_with_a_foreign_redirect_is_refused()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For("https://claude.ai/api/mcp/auth_callback");
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, "https://evil.example.org/callback",
            gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("redirect_uri", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cimd_document_that_names_another_client_is_refused()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.Document =
            """{"client_id":"https://claude.ai/other","client_name":"Claude","redirect_uris":["http://localhost/callback"]}""";
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:3118/callback",
            gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Запрос приложения не принят", page);
        Assert.Contains("invalid_client", page);
    }

    [Fact]
    public async Task Production_policy_does_not_fetch_documents_from_this_machine()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        metadata.For("http://localhost/callback");
        using var browser = Browser(gateway);

        using var response = await browser.GetAsync(Authorize(metadata.Url, "http://localhost:3118/callback",
            gateway.Issuer + "/mcp/echo"));

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

    [Theory]
    [InlineData(null)]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    public async Task Dcr_answers_with_the_registered_metadata(string? requestedMethod)
    {
        await using var gateway = await StartAsync();

        var request = new Dictionary<string, object>
        { ["client_name"] = "Claude Code", ["redirect_uris"] = new[] { "http://localhost:3118/callback" } };
        if (requestedMethod is not null)
            request["token_endpoint_auth_method"] = requestedMethod;
        using var response = await gateway.Http.PostAsJsonAsync("/connect/register", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Claude Code", json.GetProperty("client_name").GetString());
        Assert.Equal("none", json.GetProperty("token_endpoint_auth_method").GetString());
        Assert.Equal(["http://localhost:3118/callback"],
            json.GetProperty("redirect_uris").EnumerateArray().Select(uri => uri.GetString()));
        Assert.Equal(["authorization_code", "refresh_token"],
            json.GetProperty("grant_types").EnumerateArray().Select(type => type.GetString()));
        Assert.Equal(["code"], json.GetProperty("response_types").EnumerateArray().Select(type => type.GetString()));
        Assert.False(json.TryGetProperty("client_secret", out _));
    }

    [Theory]
    [InlineData(
        """{"client_name":"Bad","redirect_uris":["http://localhost/cb"],"token_endpoint_auth_method":"private_key_jwt"}""",
        "invalid_client_metadata")]
    [InlineData(
        """{"client_name":"Bad","redirect_uris":["http://localhost/cb"],"grant_types":["client_credentials"]}""",
        "invalid_client_metadata")]
    [InlineData("""{"client_name":"Bad","redirect_uris":["http://localhost/cb"],"response_types":["token"]}""",
        "invalid_client_metadata")]
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
    public async Task Dcr_refuses_more_than_ten_redirect_uris()
    {
        await using var gateway = await StartAsync();

        using var response = await gateway.Http.PostAsJsonAsync(
            "/connect/register",
            new Dictionary<string, object>
            {
                ["client_name"] = "Many",
                ["redirect_uris"] = Enumerable.Range(1, 11).Select(port => $"http://localhost:{port}/callback")
                    .ToArray()
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Dcr_refuses_an_oversized_body()
    {
        await using var gateway = await StartAsync();

        using var response = await gateway.Http.PostAsJsonAsync(
            "/connect/register",
            new Dictionary<string, object>
            {
                ["client_name"] = new string('a', 20_000),
                ["redirect_uris"] = new[] { "http://localhost/callback" }
            });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("invalid_client_metadata",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Dcr_cleans_the_app_name()
    {
        await using var gateway = await StartAsync();

        using var response = await gateway.Http.PostAsJsonAsync(
            "/connect/register",
            new Dictionary<string, object>
            {
                ["client_name"] = "  Claude\n\u0000Code  " + new string('x', 200),
                ["redirect_uris"] = new[] { "http://localhost/callback" }
            });

        var name = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_name").GetString()!;
        Assert.StartsWith("Claude Code x", name);
        Assert.Equal(101, name.Length);
    }

    [Fact]
    public async Task Unknown_client_gets_a_page_that_explains()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        using var browser = Browser(gateway);

        using var response =
            await browser.GetAsync(Authorize("nobody", OAuthFlow.Callback, gateway.Issuer + "/mcp/echo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Запрос приложения не принят", page);
        Assert.Contains("client_id", page);
    }

    [Fact]
    public async Task Resource_matches_ignoring_scheme_and_host_case_and_trailing_slash_of_public_url()
    {
        await using var gateway = await StartAsync("/");
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
        await gateway.ArrangeAsync(services =>
            services.GetRequiredService<InboundService>().SetEnabledAsync("docs", false));
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
        using var twice = await browser.GetAsync(Authorize(clientId, Callback, gateway.Issuer + "/mcp/echo") +
                                                 "&resource=" + Uri.EscapeDataString(gateway.Issuer + "/mcp/docs"));

        Assert.Equal("invalid_target", RedirectError(missing, Callback));
        Assert.Equal("invalid_target", RedirectError(twice, Callback));
    }

    [Fact]
    public async Task Token_request_without_resource_is_invalid_target()
    {
        await using var gateway = await StartAsync();
        var clientId = await RegisterAsync(gateway, "Claude Code", Callback);

        using var response = await gateway.Http.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = "not-a-code",
                ["redirect_uri"] = Callback,
                ["code_verifier"] = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_target",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task App_identity_groups_registrations_by_name_and_redirects()
    {
        await using var metadata = await MetadataHost.StartAsync();
        await using var gateway = await StartAsync();
        var dcr = await RegisterAsync(gateway, "Cursor", "cursor://anysphere.cursor-mcp/oauth/callback", Callback);
        metadata.For("http://localhost/callback");
        var cimdKnown = await gateway.ChangeAsync(services =>
            services.GetRequiredService<RegisteredApps>().EnsureFromDocumentAsync(metadata.Url, default));

        var dynamic = await gateway.ChangeAsync(services =>
            services.GetRequiredService<RegisteredApps>().IdentityAsync(dcr, default));
        var document = await gateway.ChangeAsync(services =>
            services.GetRequiredService<RegisteredApps>().IdentityAsync(metadata.Url, default));

        Assert.True(cimdKnown);
        Assert.StartsWith(AppIdentity.DynamicPrefix, dynamic!.Key);
        Assert.Equal("Cursor", dynamic.Name);
        Assert.Equivalent(new[] { "cursor://anysphere.cursor-mcp", "127.0.0.1" }, dynamic.RedirectHosts);
        Assert.False(dynamic.LoopbackOnly);
        Assert.Equal(metadata.Url, document!.Key);
        Assert.Equal("Claude Code", document.Name);
        Assert.True(document.LoopbackOnly);
        Assert.Null(await gateway.ChangeAsync(services =>
            services.GetRequiredService<RegisteredApps>().IdentityAsync("nobody", default)));
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
