using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;
using static CtxUi.IntegrationTests.OAuthPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelOAuthTests
{
    private const string MainSwitch = "Вход через браузер (OAuth)";

    [Fact]
    public async Task Oauth_switch_turns_on_and_metadata_appears()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        using var before = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/echo");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenAsync(session);

        await Switch(page, MainSwitch).CheckAsync();
        await Expect(page.GetByText("Вход через браузер включён")).ToBeVisibleAsync();
        using var after = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/echo");

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Oauth_switch_is_disabled_for_an_http_public_url()
    {
        await using var gateway = await StartAsync("http://mcp.example.org");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenAsync(session);

        await Expect(Switch(page, MainSwitch)).ToBeDisabledAsync();
        await Expect(page.GetByText("публичный адрес с https://")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Provider_card_shows_the_callback_and_saves_without_restart()
    {
        await using var gateway = await StartAsync();
        await EnableAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenAsync(session);

        var card = await OpenCardAsync(page, "GitHub");
        await Expect(card.Locator("output")).ToHaveTextAsync($"{PublicUrl}/connect/callback/github");
        await card.GetByRole(AriaRole.Textbox, new() { Name = "Client ID", Exact = true }).FillAsync("gh-client");
        await card.GetByLabel("Client secret", new() { Exact = true }).FillAsync("gh-secret");
        await card.GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();

        await Expect(page.GetByText("Провайдер GitHub сохранён")).ToBeVisibleAsync();
        var applied = gateway.App.Services.GetRequiredService<CtxUi.Core.Routing.ISnapshotSource>().Current.SignIn.Providers;
        var provider = Assert.Single(applied);
        Assert.Equal(("github", "gh-client", "gh-secret"), (provider.Name, provider.ClientId, provider.Secret));
    }

    [Fact]
    public async Task Turning_off_warns_about_clients_without_direct_key()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>()
            .AddAsync("Иванов", ["echo"], access: new ClientAccessPolicy(null, false)));
        await EnableAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenAsync(session);

        await Switch(page, MainSwitch).UncheckAsync();

        await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Клиентов без постоянного ключа: 1");
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Выключить" }).ClickAsync();
        await Expect(page.GetByText("Вход через браузер выключен")).ToBeVisibleAsync();
        Assert.False((await gateway.ChangeAsync(services => services.GetRequiredService<SignInSettingsService>().GetAsync())).Enabled);
    }

    [Fact]
    public async Task Last_method_cannot_be_turned_off()
    {
        await using var gateway = await StartAsync();
        await EnableAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenAsync(session);

        await Switch(page, "Вход по ключу").UncheckAsync();

        await Expect(page.GetByText("должен оставаться хотя бы один способ входа")).ToBeVisibleAsync();
        await Expect(Switch(page, "Вход по ключу")).ToBeCheckedAsync();
        Assert.True((await gateway.ChangeAsync(services => services.GetRequiredService<SignInSettingsService>().GetAsync())).KeySignIn);
    }
}
