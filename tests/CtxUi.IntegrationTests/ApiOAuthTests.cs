using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public class ApiOAuthTests
{
    private static Task<GatewayHost> StartAsync(string? publicUrl = null) => GatewayHost.StartAsync(builder =>
    {
        builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00";
        if (publicUrl is not null)
            builder.Configuration["CtxUi:PublicUrl"] = publicUrl;
    });

    [Fact]
    public async Task Oauth_view_never_contains_secrets()
    {
        await using var gateway = await StartAsync("https://mcp.example.org/");
        using var api = await ApiClient.IssueAsync(gateway);
        (await api.PutAsync("api/v1/oauth/providers/github", new { clientId = "gh-id", clientSecret = "gh-secret-value" })).Dispose();

        using var response = await api.GetAsync("api/v1/oauth");
        var text = await response.Content.ReadAsStringAsync();
        var view = JsonDocument.Parse(text).RootElement;
        var provider = Assert.Single(view.GetProperty("providers").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("gh-secret-value", text);
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.True(view.GetProperty("available").GetBoolean());
        Assert.True(view.GetProperty("keySignIn").GetBoolean());
        Assert.Equal(30, view.GetProperty("idleDays").GetInt32());
        Assert.Equal("github", provider.GetProperty("name").GetString());
        Assert.Equal("GitHub", provider.GetProperty("title").GetString());
        Assert.Equal("gh-id", provider.GetProperty("clientId").GetString());
        Assert.True(provider.GetProperty("secretSet").GetBoolean());
        Assert.Equal("https://mcp.example.org/connect/callback/github", provider.GetProperty("callbackUrl").GetString());
    }

    [Fact]
    public async Task Patch_enables_oauth_and_the_gateway_answers_with_resource_metadata_at_once()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        using var api = await ApiClient.IssueAsync(gateway);
        using var before = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/echo");

        using var patched = await api.PatchAsync("api/v1/oauth", new { enabled = true, idleDays = 10, defaultMaxDevices = "3", defaultDirectKey = false });
        var view = await ApiClient.ReadAsync(patched);
        using var after = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/echo");

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, view.GetProperty("idleDays").GetInt32());
        Assert.Equal(3, view.GetProperty("defaultMaxDevices").GetInt32());
        Assert.False(view.GetProperty("defaultDirectKey").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Patch_none_removes_the_default_device_limit()
    {
        await using var gateway = await StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);
        (await api.PatchAsync("api/v1/oauth", new { defaultMaxDevices = "3" })).Dispose();

        using var patched = await api.PatchAsync("api/v1/oauth", new { defaultMaxDevices = "none" });

        Assert.Equal(JsonValueKind.Null, (await ApiClient.ReadAsync(patched)).GetProperty("defaultMaxDevices").ValueKind);
    }

    [Fact]
    public async Task Patch_enable_on_http_public_url_is_400()
    {
        await using var gateway = await StartAsync("http://mcp.example.org");
        using var api = await ApiClient.IssueAsync(gateway);

        using var refused = await api.PatchAsync("api/v1/oauth", new { enabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("https://", (await ApiClient.ReadAsync(refused)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_provider_then_disable_then_delete()
    {
        await using var gateway = await StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var put = await api.PutAsync("api/v1/oauth/providers/github", new { clientId = "gh-id", clientSecret = "gh-secret" });
        using var disabled = await api.PostAsync("api/v1/oauth/providers/github/disable");
        var afterDisable = (await gateway.ChangeAsync(services => services.GetRequiredService<SignInSettingsService>().ProvidersAsync())).Single();
        using var enabled = await api.PostAsync("api/v1/oauth/providers/github/enable");
        using var changed = await api.PutAsync("api/v1/oauth/providers/github", new { clientId = "gh-id-2" });
        using var removed = await api.DeleteAsync("api/v1/oauth/providers/github");
        using var again = await api.DeleteAsync("api/v1/oauth/providers/github");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.False(afterDisable.Enabled);
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        Assert.Equal("gh-id-2", (await ApiClient.ReadAsync(changed)).GetProperty("clientId").GetString());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Empty(await gateway.ChangeAsync(services => services.GetRequiredService<SignInSettingsService>().ProvidersAsync()));
    }

    [Fact]
    public async Task New_provider_without_a_secret_is_400()
    {
        await using var gateway = await StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var refused = await api.PutAsync("api/v1/oauth/providers/github", new { clientId = "gh-id" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Unknown_provider_is_400()
    {
        await using var gateway = await StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var refused = await api.PutAsync("api/v1/oauth/providers/myspace", new { clientId = "id", clientSecret = "secret" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
