using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.ApiKeys;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class ExtensionSlotsTests
{
    private const string Failed = "Не получилось. Попробуйте ещё раз.";

    private static readonly Dictionary<string, string> Sections = new()
    {
        ["Обзор"] = "overview",
        ["Метрики"] = "metrics",
        ["Inbound'ы"] = "inbounds",
        ["Клиенты"] = "clients",
        ["Журнал"] = "journal",
        ["OAuth"] = "oauth",
        ["Ключи API"] = "keys",
    };

    private static Task<GatewayHost> StartAsync(string flag = "Slots") =>
        SampleExtensionHost.StartAsync(new Dictionary<string, string> { [$"CtxUi:Extensions:sample:{flag}"] = "true" });

    private static async Task ArrangeTwoClientsAsync(GatewayHost gateway)
    {
        await gateway.AddInboundAsync("echo", "http://127.0.0.1:1/mcp");
        await gateway.AddInboundAsync("second", "http://127.0.0.1:1/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.AddClientAsync("Петров", ["echo"]);
    }

    private static Task OpenAsync(IPage page, string link) =>
        page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = link }).ClickAsync();

    private static async Task<string[]> MenuOfAsync(IPage page, ILocator row)
    {
        await row.GetByRole(AriaRole.Button, new() { Name = "Ещё действия" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Menuitem).First).ToBeVisibleAsync();
        return [.. (await page.GetByRole(AriaRole.Menuitem).AllInnerTextsAsync()).Select(text => text.Trim())];
    }

    private static Task<string[]> TestIdsAsync(IPage page, string selector) =>
        page.Locator(selector).EvaluateAllAsync<string[]>("items => items.map(item => item.dataset.testid)");

    [Fact]
    public async Task A_block_appears_at_the_top_and_bottom_of_a_built_in_page()
    {
        await using var gateway = await StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        foreach (var (link, section) in Sections)
        {
            await OpenAsync(session.Page, link);
            await Expect(session.Page.GetByTestId($"top-{section}")).ToBeVisibleAsync();
            await Expect(session.Page.GetByTestId($"bottom-{section}")).ToBeVisibleAsync();
            Assert.Equal(
                [$"top-{section}", $"bottom-{section}"],
                await TestIdsAsync(session.Page, "[data-testid^='top-'],[data-testid^='bottom-']"));
        }
    }

    [Fact]
    public async Task A_block_in_the_client_sheet_gets_that_client()
    {
        await using var gateway = await StartAsync();
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await ClientPages.OpenClientsAsync(session);

        await RowMenu.ChooseAsync(ClientPages.Row(page, "Петров"), "Изменить");

        await Expect(ClientPages.Dialog(page).GetByTestId("client-sheet")).ToHaveTextAsync("Петров");
    }

    [Fact]
    public async Task A_block_in_the_inbound_sheet_gets_that_inbound()
    {
        await using var gateway = await StartAsync();
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        await OpenAsync(session.Page, "Inbound'ы");

        var row = ClientPages.Row(session.Page, "/mcp/second");
        await row.GetByRole(AriaRole.Button, new() { Name = "Изменить" }).ClickAsync();

        await Expect(ClientPages.Dialog(session.Page).GetByTestId("inbound-sheet")).ToHaveTextAsync("second");
    }

    [Fact]
    public async Task A_new_record_form_has_no_sheet_block()
    {
        await using var gateway = await StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        await OpenAsync(session.Page, "Inbound'ы");

        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Добавить" }).First.ClickAsync();

        await Expect(ClientPages.Dialog(session.Page)).ToBeVisibleAsync();
        await Expect(session.Page.GetByTestId("inbound-sheet")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_row_action_gets_the_chosen_client()
    {
        await using var gateway = await StartAsync();
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await ClientPages.OpenClientsAsync(session);

        var menu = await MenuOfAsync(page, ClientPages.Row(page, "Петров"));
        Assert.True(Array.IndexOf(menu, "Show client") >= 0 && Array.IndexOf(menu, "Show client") < Array.IndexOf(menu, "Удалить"));
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Show client" }).ClickAsync();

        await Expect(page.GetByText("client:Петров")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_row_action_gets_the_chosen_inbound()
    {
        await using var gateway = await StartAsync();
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        await OpenAsync(session.Page, "Inbound'ы");

        var menu = await MenuOfAsync(session.Page, ClientPages.Row(session.Page, "/mcp/second"));
        Assert.Equal(["Show inbound", "Удалить"], menu);
        await session.Page.GetByRole(AriaRole.Menuitem, new() { Name = "Show inbound" }).ClickAsync();

        await Expect(session.Page.GetByText("inbound:second")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_row_action_gets_the_chosen_api_key()
    {
        await using var gateway = await StartAsync();
        await gateway.ArrangeAsync(async services =>
        {
            var keys = services.GetRequiredService<ApiKeyService>();
            await keys.AddAsync("first");
            await keys.AddAsync("second");
        });
        await using var session = await PanelSession.SignInAsync(gateway);
        await OpenAsync(session.Page, "Ключи API");

        var menu = await MenuOfAsync(session.Page, ClientPages.Row(session.Page, "second"));
        Assert.Equal("Show key", menu[0]);
        await session.Page.GetByRole(AriaRole.Menuitem, new() { Name = "Show key" }).ClickAsync();

        await Expect(session.Page.GetByText("key:second")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_sheet_action_opens_the_sheet_for_the_chosen_row()
    {
        await using var gateway = await StartAsync();
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await ClientPages.OpenClientsAsync(session);

        await RowMenu.ChooseAsync(ClientPages.Row(page, "Петров"), "Open sheet");

        await Expect(ClientPages.Dialog(page).GetByRole(AriaRole.Heading, new() { Name = "Sheet of Петров" })).ToBeVisibleAsync();
        await Expect(ClientPages.Dialog(page).GetByTestId("action-sheet")).ToHaveTextAsync("Петров");
    }

    [Theory]
    [InlineData("Обзор", "overview")]
    [InlineData("OAuth", "oauth")]
    [InlineData("Клиенты", "clients")]
    public async Task A_page_action_appears_in_the_page_head(string link, string section)
    {
        await using var gateway = await StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        await OpenAsync(session.Page, link);

        await session.Page.Locator(".head .actions").GetByRole(AriaRole.Button, new() { Name = "Page action" }).ClickAsync();

        await Expect(session.Page.GetByText($"page:{section}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Blocks_stand_in_their_order()
    {
        await using var gateway = await StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        await OpenAsync(session.Page, "Ключи API");
        await Expect(session.Page.GetByTestId("order-one")).ToBeVisibleAsync();

        Assert.Equal(["order-one", "order-two"], await TestIdsAsync(session.Page, "[data-testid^='order-']"));
    }

    [Fact]
    public async Task A_throwing_block_stays_in_its_frame()
    {
        await using var gateway = await StartAsync("BoomBlock");
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await ClientPages.OpenClientsAsync(session);

        await Expect(page.Locator(".panel-block .empty")).ToBeVisibleAsync();
        await Expect(ClientPages.Row(page, "Петров")).ToBeVisibleAsync();
        await RowMenu.ChooseAsync(ClientPages.Row(page, "Петров"), "Изменить");
        await Expect(ClientPages.Dialog(page)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_throwing_action_shows_an_error_and_keeps_the_page()
    {
        await using var gateway = await StartAsync("BoomAction");
        await ArrangeTwoClientsAsync(gateway);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await ClientPages.OpenClientsAsync(session);

        await RowMenu.ChooseAsync(ClientPages.Row(page, "Петров"), "Boom");

        await Expect(page.GetByText(Failed)).ToBeVisibleAsync();
        await Expect(ClientPages.Row(page, "Иванов")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_slot_with_nothing_in_it_adds_no_markup()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        await Expect(session.Page.Locator(".head")).ToBeVisibleAsync();

        await Expect(session.Page.Locator(".panel-block")).ToHaveCountAsync(0);
        await Expect(session.Page.Locator(".head .actions > *")).ToHaveCountAsync(0);
    }
}
