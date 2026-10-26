using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelShellTests
{
    [Fact]
    public async Task Current_section_is_marked_in_the_navigation()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var navigation = session.Page.GetByRole(AriaRole.Navigation);

        await navigation.GetByRole(AriaRole.Link, new() { Name = "Журнал" }).ClickAsync();

        await Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "Журнал" })).ToHaveAttributeAsync("aria-current", "page");
        await Expect(navigation.GetByRole(AriaRole.Link, new() { Name = "Обзор" })).Not.ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task On_a_narrow_screen_the_admin_name_opens_the_account_menu()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        await page.SetViewportSizeAsync(390, 844);
        await Expect(page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" })).ToBeInViewportAsync();

        await page.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Button, new() { Name = "root" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Выйти" }).ClickAsync();

        await Expect(page.GetByText("Выйти из панели?")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Address_that_leads_nowhere_says_so_and_offers_the_way_back()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GotoAsync("no-such-page");

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Такой страницы нет" })).ToBeVisibleAsync();
        await Expect(page).ToHaveTitleAsync("Страница не найдена · ctx-ui");
        await page.GetByRole(AriaRole.Link, new() { Name = "Вернуться на главную" }).ClickAsync();
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

    [Fact]
    public async Task Lists_do_not_scroll_the_page_sideways_on_a_tablet()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("learn", "https://learn.example.org/mcp");
        await gateway.AddInboundAsync("docs", "https://docs.example.org/mcp");
        await gateway.AddInboundAsync("github", "https://github.example.org/mcp");
        await gateway.AddClientAsync(
            "Бот проверки домашних заданий",
            ["learn", "docs", "github"],
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero),
            limits: new CtxUi.Core.Clients.ClientLimits(QuotaCalls: 5000));
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        await page.SetViewportSizeAsync(768, 900);

        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(2);

        Assert.Equal(0, await page.EvaluateAsync<int>("scrollTo(innerWidth, scrollY) ?? scrollX"));
    }

    [Fact]
    public async Task Keyboard_focus_is_always_visible()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var link = session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" });

        await link.FocusAsync();
        await session.Page.Keyboard.PressAsync("Shift+Tab");
        await session.Page.Keyboard.PressAsync("Tab");

        await Expect(link).ToBeFocusedAsync();
        await Expect(link).ToHaveCSSAsync("outline-style", "solid");
    }
}
