using System.Net;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelLogoutTests
{
    [Fact]
    public async Task Logout_from_the_bar_closes_the_panel()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();
        await Expect(page.GetByText("Выйти из панели?")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();

        await Expect(page.GetByLabel("Пароль")).ToBeVisibleAsync();
        await page.GotoAsync("");
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
    }

    [Fact]
    public async Task Changing_ones_mind_at_logout_returns_to_the_panel()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Остаться" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Обзор" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);
        using var signedIn = await panel.SignInAsync("root", admin.Password);

        using var signedOut = await panel.SignOutAsync();
        using var home = await panel.GetAsync("");

        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal(gateway.PanelUrl + "login", signedOut.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
    }
}
