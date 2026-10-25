using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OpenIddict.Abstractions;
using CtxUi.Connect;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public class OAuthDeviceTests
{
    private static async Task<(OAuthFlow Flow, string Access, string Refresh)> ConnectAsync(
        GatewayHost gateway,
        string key,
        string slug = "echo",
        CookieContainer? cookies = null)
    {
        var flow = await OAuthFlow.StartAsync(gateway, slug, cookies);
        await flow.SignInWithKeyAsync(key);
        await flow.ApproveAsync();
        var (access, refresh) = await flow.ExchangeAsync();
        return (flow, access, refresh);
    }

    private static Task<List<Device>> DevicesAsync(GatewayHost gateway, Guid clientId) => gateway.ChangeAsync(services =>
        services.GetRequiredService<DeviceService>().ListAsync(clientId, DateTimeOffset.MinValue));

    private static Task SetLimitAsync(GatewayHost gateway, Guid clientId, int? limit) =>
        gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>()
            .UpdateAsync(clientId, new ClientUpdate { Access = new ClientAccessPolicy(limit, true) }));

    private static async Task<HttpStatusCode> CallAsync(GatewayHost gateway, string access, string slug = "echo")
    {
        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/" + slug, access, """{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
        return response.StatusCode;
    }

    [Fact]
    public async Task Limit_page_lists_devices_and_replacement_lets_the_new_one_in()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await SetLimitAsync(gateway, issued.Client.Id, 1);
        var (laptop, oldAccess, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = laptop;
        var old = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        using var phone = await OAuthFlow.StartAsync(gateway, "echo");

        await phone.SignInWithKeyAsync(issued.Token);
        Assert.Contains("Достигнут лимит устройств", phone.Page);
        Assert.Contains("Claude Code", phone.Page);
        Assert.Contains("Отключить и войти здесь", phone.Page);
        await phone.ReplaceAsync(old.Id);
        await phone.ApproveAsync();
        var (access, _) = await phone.ExchangeAsync();

        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, access));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(gateway, oldAccess));
        var devices = await DevicesAsync(gateway, issued.Client.Id);
        Assert.Equal(DeviceRevocation.User, devices.Single(device => device.Id == old.Id).RevokedReason);
        Assert.Equal(1, devices.Count(device => device.RevokedAt is null));
    }

    [Fact]
    public async Task Replacement_is_hidden_when_self_revoke_is_off()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await SetLimitAsync(gateway, issued.Client.Id, 1);
        await gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .UpdateAsync(new SignInSettingsUpdate { SelfRevoke = false }, gateway.Issuer));
        var (laptop, _, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = laptop;
        var old = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        using var phone = await OAuthFlow.StartAsync(gateway, "echo");

        await phone.SignInWithKeyAsync(issued.Token);
        var token = OAuthFlow.Forms(phone.Page).SelectMany(form => form.Fields)
            .FirstOrDefault(field => field.Key == "__RequestVerificationToken").Value;
        using var forced = await phone.Browser.PostAsync("/connect/limit/replace", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["deviceId"] = old.Id.ToString(),
            ["returnUrl"] = new Uri(phone.AuthorizeUrl).PathAndQuery,
            ["__RequestVerificationToken"] = token ?? "",
        }));

        Assert.Contains("обратитесь к администратору", phone.Page);
        Assert.DoesNotContain("Отключить и войти здесь", phone.Page);
        Assert.NotEqual(HttpStatusCode.Found, forced.StatusCode);
        Assert.Null(Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).RevokedAt);
    }

    [Fact]
    public async Task Revoked_device_is_refused_at_once_by_the_gateway()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, access, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;
        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, access));
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));

        await gateway.ChangeAsync(async services =>
        {
            await services.GetRequiredService<DeviceService>().RevokeAsync(issued.Client.Id, device.Id, DeviceRevocation.Admin);
            await services.GetRequiredService<ISnapshotSource>().RefreshAsync();
            return true;
        });

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", access));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("device_revoked", await response.Content.ReadAsStringAsync());
        Assert.Contains("устройство отключено", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revoked_device_cannot_refresh()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, refresh) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        await gateway.ArrangeAsync(services => services.GetRequiredService<DeviceService>()
            .RevokeAsync(issued.Client.Id, device.Id, DeviceRevocation.Admin));

        var answer = await flow.RefreshAsync(refresh);

        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        Assert.Equal("invalid_grant", answer.Error);
    }

    [Fact]
    public async Task Refresh_gives_working_tokens()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, refresh) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        var answer = await flow.RefreshAsync(refresh);

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.NotEqual(refresh, answer.Refresh);
        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, answer.Access!));
    }

    [Fact]
    public async Task Reused_refresh_token_revokes_the_device()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, first) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;
        var second = await flow.RefreshAsync(first);
        Assert.Equal(HttpStatusCode.OK, second.Status);

        clock.Advance(TimeSpan.FromSeconds(31));
        var reused = await flow.RefreshAsync(first);

        Assert.Equal("invalid_grant", reused.Error);
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        Assert.Equal(DeviceRevocation.TokenReused, device.RevokedReason);
        Assert.Equal("invalid_grant", (await flow.RefreshAsync(second.Refresh!)).Error);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(gateway, second.Access!));
    }

    [Fact]
    public async Task Two_refreshes_of_one_token_within_the_leeway_keep_the_device()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, refresh) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        var answers = await Task.WhenAll(flow.RefreshAsync(refresh), flow.RefreshAsync(refresh));

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer.Status));
        Assert.Null(Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).RevokedAt);
        foreach (var answer in answers)
            Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, answer.Access!));
    }

    [Fact]
    public async Task Refresh_for_an_inbound_the_client_lost_is_invalid_grant()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo", "docs"]);
        var (echo, echoAccess, echoRefresh) = await ConnectAsync(gateway, issued.Token);
        var (docs, _, docsRefresh) = await ConnectAsync(gateway, issued.Token, "docs", echo.Cookies);
        using var _ = echo;
        using var __ = docs;

        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().UpdateAsync(issued.Client.Id, new ClientUpdate { InboundSlugs = ["docs"] });
            await services.GetRequiredService<InboundService>().SetEnabledAsync("docs", false);
        });

        Assert.Equal(HttpStatusCode.NotFound, await CallAsync(gateway, echoAccess));
        Assert.Equal("invalid_grant", (await echo.RefreshAsync(echoRefresh)).Error);
        Assert.Equal("invalid_grant", (await docs.RefreshAsync(docsRefresh)).Error);
    }

    [Fact]
    public async Task Disabled_client_cannot_refresh()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, access, refresh) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        await gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>().SetEnabledAsync(issued.Client.Id, false));

        Assert.Equal("invalid_grant", (await flow.RefreshAsync(refresh)).Error);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(gateway, access));
    }

    [Fact]
    public async Task Turning_oauth_off_and_on_keeps_devices()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, access, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        await gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .UpdateAsync(new SignInSettingsUpdate { Enabled = false }, gateway.Issuer));
        using var off = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", access));
        Assert.Equal(HttpStatusCode.Unauthorized, off.StatusCode);
        Assert.Equal("Bearer", off.Headers.WwwAuthenticate.ToString());
        Assert.Contains("token_unknown", await off.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, issued.Token));

        await gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .UpdateAsync(new SignInSettingsUpdate { Enabled = true }, gateway.Issuer));

        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, access));
        Assert.Null(Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).RevokedAt);
    }

    [Fact]
    public async Task Connect_cookies_never_reach_the_upstream()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, access, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        using var request = GatewayHost.Post("/mcp/echo", access);
        request.Headers.TryAddWithoutValidation("Cookie", "ctx-ui-connect=s; ctx-ui-connectC1=p; ctx-ui-device=d; ctx-ui-external=e; a=1");
        using var response = await gateway.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a=1", Assert.Single(upstream.Requests).Headers["Cookie"]);
    }

    [Fact]
    public async Task Same_browser_for_two_clients_makes_a_device_for_each()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var ivanov = await gateway.AddClientAsync("Иванов", ["echo"]);
        var petrov = await gateway.AddClientAsync("Петров", ["echo"]);
        var (first, _, _) = await ConnectAsync(gateway, ivanov.Token);
        using var _ = first;
        using var second = await OAuthFlow.StartAsync(gateway, "echo", first.Cookies, "Cursor");

        await second.SwitchClientAsync(petrov.Token);
        await second.ApproveAsync();
        await second.ExchangeAsync();

        var mine = Assert.Single(await DevicesAsync(gateway, ivanov.Client.Id));
        var theirs = Assert.Single(await DevicesAsync(gateway, petrov.Client.Id));
        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal(mine.MarkerHash, theirs.MarkerHash);
    }

    [Fact]
    public async Task Devices_page_lets_the_user_revoke_one()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (laptop, laptopAccess, _) = await ConnectAsync(gateway, issued.Token);
        var target = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        var (phone, phoneAccess, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = laptop;
        using var __ = phone;

        using var page = await phone.Browser.GetAsync("/connect/devices");
        var body = await page.Content.ReadAsStringAsync();
        Assert.Contains("Иванов", body);
        Assert.Contains("Это устройство", body);
        var form = OAuthFlow.Forms(body).Single(form => form.Action == $"/connect/devices/{target.Id}/revoke");
        using var revoked = await phone.Browser.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));

        Assert.Equal(HttpStatusCode.Found, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(gateway, laptopAccess));
        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, phoneAccess));
        Assert.Equal(DeviceRevocation.User, (await DevicesAsync(gateway, issued.Client.Id)).Single(device => device.Id == target.Id).RevokedReason);
    }

    [Fact]
    public async Task Signing_out_keeps_the_device()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;
        using var page = await flow.Browser.GetAsync("/connect/devices");
        var form = OAuthFlow.Forms(await page.Content.ReadAsStringAsync()).Single(form => form.Action == "/connect/signout");

        using var signedOut = await flow.Browser.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));
        using var after = await flow.Browser.GetAsync("/connect/devices");

        Assert.Equal(HttpStatusCode.Found, signedOut.StatusCode);
        Assert.StartsWith("/connect/signin", after.Headers.Location!.OriginalString);
        Assert.Null(Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).RevokedAt);
    }

    [Fact]
    public async Task Revoking_without_the_page_token_is_refused()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));

        using var forged = await flow.Browser.PostAsync($"/connect/devices/{device.Id}/revoke", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Contains("Страница устарела", await forged.Content.ReadAsStringAsync());
        Assert.Null(Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).RevokedAt);
    }

    [Fact]
    public async Task Signing_out_without_the_page_token_is_refused()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        using var forged = await flow.Browser.PostAsync("/connect/signout", new FormUrlEncodedContent([]));
        using var after = await flow.Browser.GetAsync("/connect/devices");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Cancelling_on_the_limit_page_returns_access_denied_to_the_app()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await SetLimitAsync(gateway, issued.Client.Id, 1);
        var (laptop, _, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = laptop;
        using var phone = await OAuthFlow.StartAsync(gateway, "echo");

        await phone.SignInWithKeyAsync(issued.Token);
        await phone.NavigateAsync(phone.LinkTo("/connect/authorize?"));

        Assert.Equal("access_denied", phone.Answer?.GetValueOrDefault("error"));
        Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
    }

    [Fact]
    public async Task Device_remembers_the_forwarded_browser_ip()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        flow.Browser.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        flow.Browser.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

        await flow.SignInWithKeyAsync(issued.Token);

        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        Assert.Equal("203.0.113.7", device.SignInIp);
        Assert.Equal("Chrome · Windows", device.Name);
    }

    [Fact]
    public async Task Eleventh_key_attempt_in_a_minute_is_429()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await flow.OpenAsync();
        var form = OAuthFlow.Forms(flow.Page).Single(form => form.Action == "/connect/signin");
        form.Fields["key"] = AccessToken.Generate();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 11; attempt++)
        {
            using var response = await flow.Browser.PostAsync("/connect/signin", new FormUrlEncodedContent(form.Fields));
            statuses.Add(response.StatusCode);
            if (attempt == 10)
                Assert.Contains("Слишком много попыток", await response.Content.ReadAsStringAsync());
        }

        Assert.All(statuses.Take(10), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public async Task Tokens_survive_a_restart()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, access, refresh) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        await gateway.RestartAsync();

        Assert.Equal(HttpStatusCode.OK, await CallAsync(gateway, access));
        Assert.Equal(HttpStatusCode.OK, (await flow.RefreshAsync(refresh)).Status);
    }

    [Fact]
    public async Task Removed_client_tokens_stop_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, access, refresh) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;

        await gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>().RemoveAsync(issued.Client.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(gateway, access));
        Assert.Equal("invalid_grant", (await flow.RefreshAsync(refresh)).Error);
    }

    [Fact]
    public async Task Janitor_revokes_idle_devices_and_their_authorizations()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var gateway = await OAuthHarness.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var (flow, _, _) = await ConnectAsync(gateway, issued.Token);
        using var _ = flow;
        var device = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));

        clock.Advance(TimeSpan.FromDays(31));
        await gateway.App.Services.GetRequiredService<DeviceJanitor>().SweepAsync(default);

        Assert.Equal(DeviceRevocation.Idle, Assert.Single(await DevicesAsync(gateway, issued.Client.Id)).RevokedReason);
        var status = await gateway.ChangeAsync(async services =>
        {
            var authorizations = services.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await authorizations.FindByIdAsync(Assert.Single(device.Apps).AuthorizationId);
            return await authorizations.GetStatusAsync(authorization!);
        });
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, status);
    }
}
