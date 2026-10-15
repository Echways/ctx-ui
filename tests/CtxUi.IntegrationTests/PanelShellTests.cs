using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelShellTests
{
    [Fact]
    public async Task Admin_signs_in_through_the_form_and_lands_on_the_overview()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.OpenAsync(gateway);
        var page = session.Page;
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));

        await session.FillLoginAsync("root", session.Password);

        await Expect(page).ToHaveURLAsync(gateway.PanelUrl);
        await Expect(page).ToHaveTitleAsync("Обзор");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Обзор" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Обзор" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Banner)).ToContainTextAsync("root");
    }

    [Fact]
    public async Task Wrong_password_keeps_the_visitor_at_the_form()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.OpenAsync(gateway);

        await session.FillLoginAsync("root", "not-the-password-1234");

        await Expect(session.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Неверный логин или пароль");
        await Expect(session.Page.GetByLabel("Логин")).ToHaveValueAsync("root");
        await Expect(session.Page.GetByRole(AriaRole.Navigation)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Page_is_alive_the_menu_button_folds_the_navigation()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Меню" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Navigation)).Not.ToBeInViewportAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Меню" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Navigation)).ToBeInViewportAsync();
    }

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
    public async Task Open_page_is_closed_when_the_admin_gets_a_new_password()
    {
        await using var gateway = await GatewayHost.StartAsync(
            builder => builder.Configuration["CtxUi:SessionCheckInterval"] = "00:00:00.200");
        await using var session = await PanelSession.SignInAsync(gateway);

        await CliRunner.RunAsync(gateway.DataDir, "admin", "password", "root");

        await Expect(session.Page.GetByLabel("Пароль")).ToBeVisibleAsync();
        await session.FillLoginAsync("root", session.Password);
        await Expect(session.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Неверный логин или пароль");
    }

    [Fact]
    public async Task Open_page_is_closed_when_the_admin_is_removed()
    {
        await using var gateway = await GatewayHost.StartAsync(
            builder => builder.Configuration["CtxUi:SessionCheckInterval"] = "00:00:00.200");
        await using var session = await PanelSession.SignInAsync(gateway);

        await CliRunner.RunAsync(gateway.DataDir, "admin", "remove", "root");

        await Expect(session.Page.GetByLabel("Пароль")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Address_that_leads_nowhere_says_so_and_offers_the_way_back()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GotoAsync("no-such-page");

        await Expect(page.GetByText("Такой страницы нет")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Обзор" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Обзор" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Panel_asks_nothing_from_other_sites()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.OpenAsync(gateway);
        var foreign = new List<string>();
        session.Page.Request += (_, request) =>
        {
            if (!request.Url.StartsWith(gateway.BaseUrl, StringComparison.Ordinal))
                foreign.Add(request.Url);
        };

        await session.FillLoginAsync("root", session.Password);
        await Expect(session.Page.GetByRole(AriaRole.Navigation)).ToBeVisibleAsync();

        Assert.Empty(foreign);
    }
}
