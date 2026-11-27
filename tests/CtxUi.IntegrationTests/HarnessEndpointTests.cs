using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Settings;
using CtxUi.Panel;

namespace CtxUi.IntegrationTests;

public sealed class HarnessEndpointTests
{
    private const string Global = "https://ctx.example.org:9443";
    private const string Alias = "https://tunnel.example.com:8443";
    private static readonly Uri AliasOrigin = new(Alias);
    private static readonly HarnessProfile ChatGpt = new("chatgpt-web", "ChatGPT Web", true, Alias);

    [Fact]
    public async Task Saved_global_and_profiles_apply_to_existing_and_new_inbounds_and_survive_restart()
    {
        await using var gateway = await OAuthHarness.StartAsync(builder =>
        {
            builder.Configuration["CtxUi:PublicUrl"] = "https://initial.example.org";
            builder.Configuration["CtxUi:OAuthIssuer"] = "https://issuer.example.org";
        });
        await using var upstream = await FakeUpstream.StartAsync();
        await gateway.AddInboundAsync("uni", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("client", ["uni"]);
        using var api = await ApiClient.IssueAsync(gateway);
        var connection = $"api/v1/clients/{issued.Client.Id}/connection";

        await AssertConnectionAsync(api, connection, "https://initial.example.org", "uni");
        await Assert.ThrowsAsync<DomainException>(() => UpdateAsync(gateway, settings =>
            settings.SaveGlobalAsync("https://bad.example.org/path")));

        await UpdateAsync(gateway, settings => settings.SaveGlobalAsync("https://CTX.example.org:9443//"));
        await UpdateAsync(gateway, settings => settings.SaveProfileAsync(ChatGpt));
        var custom = new HarnessProfile("custom", "Custom", true, "https://custom.example.org");
        await UpdateAsync(gateway, settings => settings.CreateProfileAsync(custom));
        await Assert.ThrowsAsync<DomainException>(() => UpdateAsync(gateway, settings =>
            settings.CreateProfileAsync(custom with { PublicUrl = Alias })));

        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        var later = await gateway.AddClientAsync("later", ["uni", "docs"]);
        var laterConnection = $"api/v1/clients/{later.Client.Id}/connection";
        await AssertConnectionAsync(api, laterConnection, Global, "docs", "uni");
        await AssertConnectionAsync(api, laterConnection + "?harness=chatgpt-web", Alias, "docs", "uni");
        await AssertConnectionAsync(api, laterConnection + "?harness=custom", custom.PublicUrl!, "docs", "uni");

        using var metadataRequest = OAuthFlow.AtOrigin(
            new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp/docs"), AliasOrigin);
        using var metadata = await gateway.Http.SendAsync(metadataRequest);
        var discovery = await metadata.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Alias + "/mcp/docs", discovery.GetProperty("resource").GetString());
        Assert.Equal("https://issuer.example.org", discovery.GetProperty("authorization_servers")[0].GetString());

        await gateway.RestartAsync();
        using var restartedApi = await ApiClient.IssueAsync(gateway, "restarted");
        await AssertConnectionAsync(restartedApi, connection, Global, "uni");
        await AssertConnectionAsync(restartedApi, connection + "?harness=chatgpt-web", Alias, "uni");
        await UpdateAsync(gateway, settings => settings.ResetProfileUrlAsync(ChatGpt.Id));
        await AssertConnectionAsync(restartedApi, connection + "?harness=chatgpt-web", Global, "uni");
        await UpdateAsync(gateway, settings => settings.SaveProfileAsync(ChatGpt with { Enabled = false }));
        using var disabled = await restartedApi.GetAsync(connection + "?harness=chatgpt-web");
        using var unknown = await restartedApi.GetAsync(connection + "?harness=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await UpdateAsync(gateway, settings => settings.ResetProfileUrlAsync(ChatGpt.Id));
        using var stillDisabled = await restartedApi.GetAsync(connection + "?harness=chatgpt-web");
        Assert.Equal(HttpStatusCode.BadRequest, stillDisabled.StatusCode);
        using var enabled = await restartedApi.GetAsync("api/v1/harnesses");
        Assert.DoesNotContain((await ApiClient.ReadAsync(enabled)).EnumerateArray(),
            profile => profile.GetProperty("id").GetString() == ChatGpt.Id);
    }

    [Fact]
    public async Task OAuth_binds_the_code_refresh_and_access_token_to_the_selected_origin_and_inbound()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await using var upstream = await McpEndToEndTests.StartMcpServerAsync();
        foreach (var slug in new[] { "uni", "docs" })
            await gateway.AddInboundAsync(slug, upstream.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("client", ["uni", "docs"]);

        using var original = await OAuthFlow.StartAsync(gateway, "uni");
        await original.SignInWithKeyAsync(issued.Token);
        await original.ApproveAsync();
        var originalTokens = await original.ExchangeAsync();

        await UpdateAsync(gateway, settings => settings.SaveProfileAsync(ChatGpt));
        using var flow = await OAuthFlow.StartAsync(gateway, "uni", clientId: original.ClientId,
            resourceOrigin: AliasOrigin);
        Assert.Equal(Alias + "/mcp/uni", flow.Resource);
        Assert.Equal(gateway.Issuer, flow.Issuer);

        var redirects = new[]
        {
            "https://tunnel.example.com/mcp/uni",
            "https://tunnel.example.com.evil.test:8443/mcp/uni",
            "https://other.example.com:8443/mcp/uni",
            Alias + "/mcp/unknown",
            Alias + "/mcp/../mcp/uni",
            Alias + "/mcp/%75ni",
            Alias + "/mcp/uni?",
            "https://user@tunnel.example.com:8443/mcp/uni"
        };
        foreach (var resource in redirects)
        {
            var url = flow.AuthorizeUrl.Replace(Uri.EscapeDataString(flow.Resource),
                Uri.EscapeDataString(resource), StringComparison.Ordinal);
            Assert.NotEqual(flow.AuthorizeUrl, url);
            await flow.NavigateAsync(url);
            Assert.Equal("invalid_target", flow.Answer!["error"]);
        }

        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        Assert.Equal("invalid_target", (await flow.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = flow.Code!,
            ["redirect_uri"] = OAuthFlow.Callback,
            ["code_verifier"] = flow.Verifier,
            ["resource"] = original.Resource
        })).Error);

        using var granted = await OAuthFlow.StartAsync(gateway, "uni", flow.Cookies, clientId: original.ClientId,
            resourceOrigin: AliasOrigin);
        await granted.OpenAsync();
        var tokens = await granted.ExchangeAsync();
        await AssertCallAsync(gateway, "uni", tokens.Access, AliasOrigin, HttpStatusCode.OK);
        await AssertCallAsync(gateway, "uni", tokens.Access, null, HttpStatusCode.Unauthorized);
        await AssertCallAsync(gateway, "docs", tokens.Access, AliasOrigin, HttpStatusCode.Unauthorized);
        await AssertCallAsync(gateway, "uni", originalTokens.Access, null, HttpStatusCode.OK);
        await AssertCallAsync(gateway, "uni", originalTokens.Access, AliasOrigin, HttpStatusCode.Unauthorized);

        var renewed = await granted.RefreshAsync(tokens.Refresh);
        Assert.Equal(HttpStatusCode.OK, renewed.Status);
        await AssertCallAsync(gateway, "uni", renewed.Access!, AliasOrigin, HttpStatusCode.OK);
        Assert.Equal("invalid_target", (await granted.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = renewed.Refresh!,
            ["resource"] = original.Resource
        })).Error);

        await gateway.ArrangeAsync(services =>
            services.GetRequiredService<InboundService>().SetEnabledAsync("docs", false));
        using var disabledInbound = OAuthFlow.AtOrigin(
            new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp/docs"), AliasOrigin);
        using var metadata = await gateway.Http.SendAsync(disabledInbound);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);

        await UpdateAsync(gateway, settings => settings.SaveProfileAsync(ChatGpt with { Enabled = false }));
        Assert.Equal("invalid_target", (await granted.RefreshAsync(renewed.Refresh!)).Error);
        await AssertCallAsync(gateway, "uni", renewed.Access!, AliasOrigin, HttpStatusCode.NotFound);
        await AssertCallAsync(gateway, "uni", originalTokens.Access, null, HttpStatusCode.OK);

        using var spoofed = OAuthFlow.AtOrigin(OAuthFlow.Mcp("uni", originalTokens.Access),
            new Uri("https://unregistered.example.org"));
        spoofed.Headers.Add("X-Forwarded-Host", new Uri(gateway.Issuer).Authority);
        using var rejected = await gateway.Http.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
    }

    private static Task UpdateAsync(GatewayHost gateway, Func<PublicEndpointSettings, Task> update)
    {
        return gateway.App.Services.GetRequiredService<PanelBackend>().ChangeAsync(services =>
            update(services.GetRequiredService<PublicEndpointSettings>()));
    }

    private static async Task AssertConnectionAsync(ApiClient api, string path, string origin, params string[] slugs)
    {
        using var response = await api.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var connection = await ApiClient.ReadAsync(response);
        var token = connection.GetProperty("token").GetString();
        var addresses = connection.GetProperty("addresses").EnumerateArray().ToArray();
        Assert.Equal(slugs, addresses.Select(item => item.GetProperty("inbound").GetString()));
        foreach (var (address, slug) in addresses.Zip(slugs))
        {
            var url = origin + "/mcp/" + slug;
            Assert.Equal(url, address.GetProperty("url").GetString());
            Assert.Equal($"{origin}/k/{token}/mcp/{slug}", address.GetProperty("urlWithToken").GetString());
            Assert.Contains(url, connection.GetProperty("message").GetString());
        }
    }

    private static async Task AssertCallAsync(GatewayHost gateway, string slug, string token, Uri? origin,
        HttpStatusCode status)
    {
        using var request = OAuthFlow.AtOrigin(OAuthFlow.Mcp(slug, token), origin);
        using var response = await gateway.Http.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
    }
}
