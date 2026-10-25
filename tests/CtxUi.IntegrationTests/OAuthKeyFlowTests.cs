using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public class OAuthKeyFlowTests
{
    private static async Task<(WebApplication Server, GatewayHost Gateway, IssuedClient Issued)> StartAsync(params string[] slugs)
    {
        var server = await McpEndToEndTests.StartMcpServerAsync();
        var gateway = await OAuthHarness.StartAsync();
        foreach (var slug in slugs.DefaultIfEmpty("echo"))
            await gateway.AddInboundAsync(slug, server.Urls.First() + "/mcp");

        return (server, gateway, await gateway.AddClientAsync("Иванов", slugs.DefaultIfEmpty("echo").ToArray()));
    }

    private static Task<List<Device>> DevicesAsync(GatewayHost gateway, Guid clientId) => gateway.ChangeAsync(services =>
        services.GetRequiredService<DeviceService>().ListAsync(clientId, DateTimeOffset.MinValue));

    [Fact]
    public async Task Key_sign_in_gives_a_token_that_calls_a_tool()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _ = server;
        await using var __ = gateway;
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");

        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        var tokens = await flow.ExchangeAsync();

        using var call = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", tokens.Access));
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        Assert.Equal(Device.ByKey, device.SignedInWith);
        Assert.Equal("Claude Code", Assert.Single(device.Apps).AppName);
    }

    [Fact]
    public async Task Wrong_key_is_refused_on_the_page()
    {
        var (server, gateway, _) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");

        await flow.SignInWithKeyAsync(AccessToken.Generate());

        Assert.Equal(HttpStatusCode.OK, flow.Status);
        Assert.Contains("Такой ключ не найден", flow.Page);
        Assert.Null(flow.Code);
    }

    [Fact]
    public async Task Disabled_client_key_is_refused_on_the_page()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        await gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>().SetEnabledAsync(issued.Client.Id, false));
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");

        await flow.SignInWithKeyAsync(issued.Token);

        Assert.Contains("Доступ по этому ключу выключен", flow.Page);
        Assert.Null(flow.Code);
    }

    [Fact]
    public async Task Key_sign_in_works_even_when_the_direct_key_is_off()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        await gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>()
            .UpdateAsync(issued.Client.Id, new ClientUpdate { Access = new ClientAccessPolicy(null, false) }));
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");

        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        var tokens = await flow.ExchangeAsync();

        using var oauth = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", tokens.Access));
        using var direct = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", issued.Token));
        Assert.Equal(HttpStatusCode.OK, oauth.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, direct.StatusCode);
    }

    [Fact]
    public async Task Key_sign_in_off_hides_the_key_field()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        await gateway.ArrangeAsync(async services =>
        {
            var settings = services.GetRequiredService<SignInSettingsService>();
            await settings.SetProviderAsync(new ProviderSetup("github", "client-id", "secret"));
            await settings.UpdateAsync(new SignInSettingsUpdate { KeySignIn = false }, gateway.Issuer);
        });
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");

        await flow.OpenAsync();
        var token = OAuthFlow.Forms(flow.Page).SelectMany(form => form.Fields).FirstOrDefault(field => field.Key == "__RequestVerificationToken");
        using var posted = await flow.Browser.PostAsync("/connect/signin", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = issued.Token,
            ["__RequestVerificationToken"] = token.Value ?? "",
        }));

        Assert.Equal(HttpStatusCode.OK, flow.Status);
        Assert.DoesNotContain("name=\"key\"", flow.Page);
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
    }

    [Fact]
    public async Task Second_app_on_the_same_browser_skips_sign_in_and_is_the_same_device()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        using var claude = await OAuthFlow.StartAsync(gateway, "echo");
        await claude.SignInWithKeyAsync(issued.Token);
        await claude.ApproveAsync();
        await claude.ExchangeAsync();
        using var cursor = await OAuthFlow.StartAsync(gateway, "echo", claude.Cookies, clientName: "Cursor");

        await cursor.OpenAsync();
        Assert.Contains("Cursor", cursor.Page);
        await cursor.ApproveAsync();
        await cursor.ExchangeAsync();
        using var again = await OAuthFlow.StartAsync(gateway, "echo", claude.Cookies, clientName: "Cursor");
        await again.OpenAsync();

        Assert.NotNull(again.Code);
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        Assert.Equivalent(new[] { "Claude Code", "Cursor" }, device.Apps.Select(app => app.AppName));
    }

    [Fact]
    public async Task Consent_shows_the_app_name_and_redirect_host_and_warns_for_loopback()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        using var local = await OAuthFlow.StartAsync(gateway, "echo");
        using var web = await OAuthFlow.StartAsync(gateway, "echo", local.Cookies, "Claude", "https://claude.ai/api/mcp/auth_callback");

        await local.SignInWithKeyAsync(issued.Token);
        await web.OpenAsync();

        Assert.Contains("Claude Code", local.Page);
        Assert.Contains("127.0.0.1", local.Page);
        Assert.Contains("echo", local.Page);
        Assert.Contains("только на этом компьютере", local.Page);
        Assert.Contains("claude.ai", web.Page);
        Assert.DoesNotContain("только на этом компьютере", web.Page);
    }

    [Fact]
    public async Task Cancelling_the_consent_returns_access_denied()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await flow.SignInWithKeyAsync(issued.Token);

        await flow.SubmitAsync("/connect/authorize");

        Assert.Equal("access_denied", flow.Answer!["error"]);
        Assert.Empty(Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).Apps);
    }

    [Fact]
    public async Task Client_without_the_inbound_sees_no_access()
    {
        var (server, gateway, _) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        await gateway.AddInboundAsync("docs", server.Urls.First() + "/mcp");
        var petrov = await gateway.AddClientAsync("Петров", ["docs"]);
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");

        await flow.SignInWithKeyAsync(petrov.Token);

        Assert.Contains("нет доступа к этому серверу", flow.Page);
        Assert.Null(flow.Code);
        Assert.Empty(await DevicesAsync(gateway, petrov.Client.Id));
    }

    [Fact]
    public async Task Signing_in_as_another_client_replaces_the_session()
    {
        var (server, gateway, ivanov) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        var petrov = await gateway.AddClientAsync("Петров", ["echo"]);
        using var first = await OAuthFlow.StartAsync(gateway, "echo");
        await first.SignInWithKeyAsync(ivanov.Token);
        await first.ApproveAsync();
        await first.ExchangeAsync();
        using var second = await OAuthFlow.StartAsync(gateway, "echo", first.Cookies, "Cursor");

        await second.SwitchClientAsync(petrov.Token);
        await second.ApproveAsync();
        var tokens = await second.ExchangeAsync();
        using var devices = await second.Browser.GetAsync("/connect/devices");

        Assert.Single(await DevicesAsync(gateway, ivanov.Client.Id));
        Assert.Single(await DevicesAsync(gateway, petrov.Client.Id));
        Assert.Contains("Петров", await devices.Content.ReadAsStringAsync());
        using var call = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", tokens.Access));
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
    }

    [Fact]
    public async Task Token_of_one_inbound_is_refused_by_another()
    {
        var (server, gateway, issued) = await StartAsync("echo", "docs");
        await using var _s = server;
        await using var _g = gateway;
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        var tokens = await flow.ExchangeAsync();

        using var other = await gateway.Http.SendAsync(OAuthFlow.Mcp("docs", tokens.Access));

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Contains("token_invalid", await other.Content.ReadAsStringAsync());
        Assert.Contains("error=\"invalid_token\"", other.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Access_token_lives_an_hour_and_refresh_token_the_idle_days()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        await gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .UpdateAsync(new SignInSettingsUpdate { IdleDays = 10 }, gateway.Issuer));
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();

        var answer = await flow.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = flow.Code!,
            ["redirect_uri"] = OAuthFlow.Callback,
            ["code_verifier"] = flow.Verifier,
        });

        Assert.InRange(answer.Body.GetProperty("expires_in").GetInt32(), 3599, 3600);
        var refresh = await gateway.ChangeAsync(async services =>
        {
            var tokens = services.GetRequiredService<IOpenIddictTokenManager>();
            await foreach (var token in tokens.ListAsync())
            {
                if (await tokens.GetTypeAsync(token) == OpenIddictConstants.TokenTypeIdentifiers.RefreshToken)
                    return (await tokens.GetCreationDateAsync(token), await tokens.GetExpirationDateAsync(token));
            }

            return (null, null);
        });
        Assert.Equal(TimeSpan.FromDays(10), refresh.Item2 - refresh.Item1);
    }

    [Fact]
    public async Task Sign_in_page_does_not_send_back_to_foreign_addresses()
    {
        var (server, gateway, issued) = await StartAsync();
        await using var _s = server;
        await using var _g = gateway;
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await flow.OpenAsync();
        var form = OAuthFlow.Forms(flow.Page).Single(form => form.Action == "/connect/signin");
        form.Fields["key"] = issued.Token;
        form.Fields["returnUrl"] = "https://evil.example.org/";

        using var posted = await flow.Browser.PostAsync("/connect/signin", new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);
        Assert.Equal("/connect/devices", posted.Headers.Location!.OriginalString);
    }
}
