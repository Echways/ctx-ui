using System.Net;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public class OAuthExternalTests
{
    private const string ProviderClientId = "gateway-client";

    private sealed record Setup(FakeOidcProvider Provider, GatewayHost Gateway, IssuedClient Issued) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Gateway.DisposeAsync();
            await Provider.DisposeAsync();
        }

        public Task<string> InviteAsync(Guid? clientId = null) =>
            Gateway.ChangeAsync(services => services.GetRequiredService<ClientLinkService>().InviteAsync(clientId ?? Issued.Client.Id));

        public Task<List<ClientLink>> LinksAsync() =>
            Gateway.ChangeAsync(services => services.GetRequiredService<ClientLinkService>().ListAsync(Issued.Client.Id));

        public Task LinkAsync(Guid clientId, string subject = "user-1") => Gateway.ChangeAsync(async services =>
        {
            var links = services.GetRequiredService<ClientLinkService>();
            var result = await links.LinkAsync(
                await links.InviteAsync(clientId),
                new ExternalIdentity("oidc", subject, "ivanov@example.org"));
            Assert.Equal(LinkOutcome.Linked, result.Outcome);
            return true;
        });
    }

    private static async Task<Setup> StartAsync(bool keySignIn = true)
    {
        var provider = await FakeOidcProvider.StartAsync();
        var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ArrangeAsync(async services =>
        {
            var settings = services.GetRequiredService<SignInSettingsService>();
            await settings.SetProviderAsync(new ProviderSetup("oidc", ProviderClientId, "secret", provider.Issuer, "Корпоративный вход"));
            await settings.UpdateAsync(new SignInSettingsUpdate { KeySignIn = keySignIn }, gateway.Issuer);
        });
        return new Setup(provider, gateway, issued);
    }

    private static async Task SignInThroughProviderAsync(OAuthFlow flow)
    {
        await flow.OpenAsync();
        await flow.NavigateAsync(flow.LinkTo("/connect/external/oidc"));
    }

    [Fact]
    public async Task Invite_links_the_account_and_signs_in()
    {
        await using var setup = await StartAsync();
        var code = await setup.InviteAsync();
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await flow.NavigateAsync($"/connect/invite/{code}");
        Assert.Contains("Иванов", flow.Page);
        await flow.NavigateAsync(flow.LinkTo("/connect/external/oidc"));

        Assert.Equal(HttpStatusCode.OK, flow.Status);
        Assert.Contains("Готово", flow.Page);
        var link = Assert.Single(await setup.LinksAsync());
        Assert.Equal(("oidc", "user-1", "ivanov@example.org"), (link.Provider, link.Subject, link.Display));
        await flow.NavigateAsync("/connect/devices");
        Assert.Contains("Иванов", flow.Page);
    }

    [Fact]
    public async Task Linked_account_signs_in_without_an_invite()
    {
        await using var setup = await StartAsync();
        await setup.LinkAsync(setup.Issued.Client.Id);
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await SignInThroughProviderAsync(flow);
        await flow.ApproveAsync();
        var tokens = await flow.ExchangeAsync();

        using var call = await setup.Gateway.Http.SendAsync(OAuthFlow.Mcp("echo", tokens.Access));
        Assert.NotEqual(HttpStatusCode.Unauthorized, call.StatusCode);
        var device = Assert.Single(await setup.Gateway.ChangeAsync(services =>
            services.GetRequiredService<DeviceService>().ListAsync(setup.Issued.Client.Id, DateTimeOffset.MinValue)));
        Assert.Equal("oidc", device.SignedInWith);
    }

    [Fact]
    public async Task Unlinked_account_is_told_to_ask_for_an_invite()
    {
        await using var setup = await StartAsync();
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await SignInThroughProviderAsync(flow);

        Assert.Contains("не привязан", flow.Page);
        Assert.Empty(await setup.LinksAsync());
        Assert.Null(flow.Code);
    }

    [Fact]
    public async Task Used_invite_is_refused()
    {
        await using var setup = await StartAsync();
        var code = await setup.InviteAsync();
        using var first = await OAuthFlow.StartAsync(setup.Gateway, "echo");
        await first.NavigateAsync($"/connect/invite/{code}");
        await first.NavigateAsync(first.LinkTo("/connect/external/oidc"));
        using var second = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await second.NavigateAsync($"/connect/invite/{code}");

        Assert.Contains("Приглашение недействительно", second.Page);
        Assert.DoesNotContain("/connect/external/oidc", second.Page);
    }

    [Fact]
    public async Task Account_linked_elsewhere_is_refused_and_the_invite_stays()
    {
        await using var setup = await StartAsync();
        var other = await setup.Gateway.AddClientAsync("Петров", ["echo"]);
        await setup.LinkAsync(other.Client.Id);
        var code = await setup.InviteAsync();
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await flow.NavigateAsync($"/connect/invite/{code}");
        await flow.NavigateAsync(flow.LinkTo("/connect/external/oidc"));

        Assert.Contains("уже привязан к другому", flow.Page);
        Assert.Empty(await setup.LinksAsync());
        Assert.NotNull(await setup.Gateway.ChangeAsync(services => services.GetRequiredService<ClientLinkService>().InvitedClientAsync(code)));
    }

    [Fact]
    public async Task Disabled_provider_is_404_and_hidden_on_the_sign_in_page()
    {
        await using var setup = await StartAsync();
        await setup.Gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>().SetProviderEnabledAsync("oidc", false));
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await flow.OpenAsync();
        var page = flow.Page;
        await flow.NavigateAsync("/connect/external/oidc");

        Assert.DoesNotContain("/connect/external/oidc", page);
        Assert.Equal(HttpStatusCode.NotFound, flow.Status);
    }

    [Fact]
    public async Task Changing_the_provider_client_id_applies_without_restart()
    {
        await using var setup = await StartAsync();
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");
        await flow.OpenAsync();
        var link = flow.LinkTo("/connect/external/oidc");

        using var first = await flow.Browser.GetAsync(link);
        Assert.Contains($"client_id={ProviderClientId}&", first.Headers.Location!.Query.TrimStart('?') + "&");
        await setup.Gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .SetProviderAsync(new ProviderSetup("oidc", "renamed-client", null, setup.Provider.Issuer, "Корпоративный вход")));

        using var second = await flow.Browser.GetAsync(link);

        Assert.Contains("client_id=renamed-client&", second.Headers.Location!.Query.TrimStart('?') + "&");
    }

    [Fact]
    public async Task Only_providers_left_works_with_key_sign_in_off()
    {
        await using var setup = await StartAsync(keySignIn: false);
        await setup.LinkAsync(setup.Issued.Client.Id);
        using var flow = await OAuthFlow.StartAsync(setup.Gateway, "echo");

        await flow.OpenAsync();
        Assert.DoesNotContain("name=\"key\"", flow.Page);
        await flow.NavigateAsync(flow.LinkTo("/connect/external/oidc"));
        await flow.ApproveAsync();

        Assert.NotNull(flow.Code);
    }
}
