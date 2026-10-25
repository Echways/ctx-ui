using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public class OAuthMetadataTests
{
    private static string[] Strings(JsonElement json, string name) =>
        json.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Fact]
    public async Task Metadata_is_404_while_oauth_is_off()
    {
        await using var gateway = await GatewayHost.StartAsync();

        foreach (var path in new[]
                 {
                     "/.well-known/oauth-authorization-server",
                     "/.well-known/openid-configuration",
                     "/.well-known/oauth-protected-resource/mcp/learn",
                     "/connect/authorize",
                     "/connect/token",
                 })
        {
            using var response = await gateway.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Server_metadata_advertises_what_mcp_clients_require()
    {
        await using var gateway = await OAuthHarness.StartAsync();

        var json = await gateway.Http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        Assert.Equal(gateway.Issuer, json.GetProperty("issuer").GetString());
        Assert.True(json.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.True(json.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.Contains("none", Strings(json, "token_endpoint_auth_methods_supported"));
        Assert.Equal(["S256"], Strings(json, "code_challenge_methods_supported"));
        Assert.Equal(gateway.Issuer + "/connect/register", json.GetProperty("registration_endpoint").GetString());
        Assert.Equal(gateway.Issuer + "/connect/authorize", json.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(gateway.Issuer + "/connect/token", json.GetProperty("token_endpoint").GetString());
        Assert.Equivalent(new[] { "mcp", "offline_access" }, Strings(json, "scopes_supported"));
        Assert.Equivalent(new[] { "authorization_code", "refresh_token" }, Strings(json, "grant_types_supported"));
        Assert.Equal(["code"], Strings(json, "response_types_supported"));
    }

    [Fact]
    public async Task Endpoints_follow_the_public_url_whatever_the_host_header_says()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-authorization-server");
        request.Headers.Host = "other.example.org";

        using var response = await gateway.Http.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var name in new[] { "authorization_endpoint", "token_endpoint", "revocation_endpoint", "jwks_uri" })
            Assert.StartsWith(gateway.Issuer + "/", json.GetProperty(name).GetString());
    }

    [Fact]
    public async Task Openid_configuration_serves_the_same_issuer()
    {
        await using var gateway = await OAuthHarness.StartAsync();

        var json = await gateway.Http.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.Equal(gateway.Issuer, json.GetProperty("issuer").GetString());
        Assert.Equal(gateway.Issuer + "/connect/register", json.GetProperty("registration_endpoint").GetString());
    }

    [Fact]
    public async Task Resource_metadata_names_the_inbound_and_the_issuer()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.ArrangeAsync(services => services.GetRequiredService<InboundService>()
            .AddHttpAsync("learn", "Обучение", "https://mcp.example.org/mcp"));

        var json = await gateway.Http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp/learn");

        Assert.Equal(gateway.Issuer + "/mcp/learn", json.GetProperty("resource").GetString());
        Assert.Equal([gateway.Issuer], Strings(json, "authorization_servers"));
        Assert.Equal(["mcp"], Strings(json, "scopes_supported"));
        Assert.Equal(["header"], Strings(json, "bearer_methods_supported"));
        Assert.Equal("Обучение", json.GetProperty("resource_name").GetString());
    }

    [Fact]
    public async Task Resource_metadata_for_an_unknown_slug_does_not_reveal_it()
    {
        await using var gateway = await OAuthHarness.StartAsync();

        using var unknown = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/secret-project");
        using var invalid = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/Bad_Slug");

        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        var json = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("secret-project", json.GetProperty("resource_name").GetString());
        Assert.Equal(gateway.Issuer + "/mcp/secret-project", json.GetProperty("resource").GetString());
        Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
    }

    [Fact]
    public async Task Gateway_401_points_to_resource_metadata_once_oauth_is_on()
    {
        await using var gateway = await OAuthHarness.StartAsync();

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/learn"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{gateway.Issuer}/.well-known/oauth-protected-resource/mcp/learn\", scope=\"mcp\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Turning_oauth_off_hides_the_metadata_at_once()
    {
        await using var gateway = await OAuthHarness.StartAsync();

        await gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .UpdateAsync(new SignInSettingsUpdate { Enabled = false }, gateway.Issuer));

        Assert.Equal(HttpStatusCode.NotFound, (await gateway.Http.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/learn"));
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Token_keys_are_private_and_kept_across_restarts()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        var keys = Path.Combine(gateway.DataDir, "keys");
        string[] names = ["oauth-signing.key", "oauth-encryption.key"];
        var before = names.Select(name => File.ReadAllBytes(Path.Combine(keys, name))).ToList();

        await gateway.RestartAsync();
        await gateway.Http.GetAsync("/.well-known/oauth-authorization-server");

        Assert.Equal(before, names.Select(name => File.ReadAllBytes(Path.Combine(keys, name))));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var name in names)
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(keys, name)));
        }

        Assert.DoesNotContain("RSA", Encoding.UTF8.GetString(before[0]));
    }

    [Fact]
    public async Task Oauth_on_with_an_http_public_url_stays_off()
    {
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:PublicUrl"] = "http://mcp.example.org");
        await gateway.ArrangeAsync(async services =>
        {
            var db = services.GetRequiredService<CtxUiDbContext>();
            (await db.SignInSettings.FindAsync(1))!.Enabled = true;
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.NotFound, (await gateway.Http.GetAsync("/.well-known/oauth-authorization-server")).StatusCode);
        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/learn"));
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }
}
