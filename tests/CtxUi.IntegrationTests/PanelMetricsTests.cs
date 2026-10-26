using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static CtxUi.IntegrationTests.JournalPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelMetricsTests
{
    private static ILocator Figure(IPage page, string name) => page.GetByRole(AriaRole.Figure, new() { Name = name });

    private static ILocator Row(IPage page, string text) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = text });

    private static ILocator Row(ILocator scope, string text) => scope.GetByRole(AriaRole.Row).Filter(new() { HasText = text });

    private static ILocator Section(IPage page, string heading) =>
        page.Locator("section").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = heading, Exact = true }) });

    private static ILocator Filter(IPage page) => page.GetByRole(AriaRole.Navigation, new() { Name = "Фильтр" });

    private static async Task<(GatewayHost Gateway, FakeUpstream Upstream)> StartWithCallsAsync()
    {
        var upstream = await FakeUpstream.StartAsync((app, _) =>
            app.Map("/broken", context =>
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return Task.CompletedTask;
            }));
        var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("broken", upstream.BaseUrl + "/broken");
        var ivanov = await gateway.AddClientAsync("Иванов", ["echo", "broken"]);
        var petrov = await gateway.AddClientAsync("Петров", ["echo"]);
        foreach (var (token, slug, body) in new[]
                 {
                     (ivanov.Token, "echo", ToolCall("search")),
                     (ivanov.Token, "echo", ToolCall("search")),
                     (ivanov.Token, "echo", ToolsList),
                     (ivanov.Token, "broken", ToolCall("fetch")),
                     (petrov.Token, "echo", ToolCall("search")),
                 })
        {
            using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/" + slug, token, body));
        }

        await JournalHasAsync(gateway, 5);
        return (gateway, upstream);
    }

    [Fact]
    public async Task Rows_lead_down_to_a_client_and_a_tool_the_period_keeps_the_filter_and_crumbs_take_it_off()
    {
        var (gateway, upstream) = await StartWithCallsAsync();
        await using var _ = upstream;
        await using var __ = gateway;
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        var periods = page.GetByRole(AriaRole.Navigation, new() { Name = "Период" });

        await page.GotoAsync("metrics?days=999");

        await Expect(periods.GetByRole(AriaRole.Link)).ToHaveTextAsync(["Сутки", "7 дней", "30 дней"]);
        await Expect(periods.Locator("[aria-current=page]")).ToHaveTextAsync("7 дней");
        await Expect(page.GetByRole(AriaRole.Img, new() { Name = "Вызовы за последние 7 дней по 2 часа" })).ToBeVisibleAsync();
        await Expect(Figure(page, "5 вызовов")).ToContainTextAsync("4 из них к инструментам");
        await Expect(Figure(page, "1 ошибка")).ToContainTextAsync("20 % вызовов");
        await Expect(Figure(page, "2 клиента заходили")).ToContainTextAsync("из 2 с доступом");
        await Expect(Section(page, "Путь клиентов")).ToContainTextAsync("Получили список инструментов 1 · 50 %");
        await Expect(Row(Section(page, "Частые ошибки"), "tools/call fetch")).ToContainTextAsync("сбой upstream HTTP 502");
        await Expect(Filter(page)).ToHaveCountAsync(0);

        await Row(page, "Иванов").ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex("client="));
        await Expect(Filter(page)).ToContainTextAsync("Клиент Иванов");
        await Expect(Figure(page, "4 вызова")).ToBeVisibleAsync();
        await Expect(Section(page, "Клиенты")).ToHaveCountAsync(0);
        await Expect(Section(page, "Путь клиентов")).ToHaveCountAsync(0);

        await Row(Section(page, "Инструменты"), "search").ClickAsync();

        await Expect(Filter(page)).ToContainTextAsync("Inbound echo");
        await Expect(Filter(page)).ToContainTextAsync("Инструмент search");
        await Expect(Figure(page, "2 вызова")).ToContainTextAsync("2 из них к инструментам");

        await periods.GetByRole(AriaRole.Link, new() { Name = "Сутки" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex("days=1&client=.+&inbound=.+&tool=search"));
        await Expect(periods.Locator("[aria-current=page]")).ToHaveTextAsync("Сутки");
        await Expect(page.GetByRole(AriaRole.Img, new() { Name = "Вызовы за последние сутки по 15 минут" })).ToBeVisibleAsync();
        await Expect(Filter(page)).ToContainTextAsync("Клиент Иванов");
        await Expect(Figure(page, "2 вызова")).ToBeVisibleAsync();

        await Filter(page).GetByRole(AriaRole.Link, new() { Name = "Убрать фильтр Клиент: Иванов" }).ClickAsync();

        await Expect(Figure(page, "3 вызова")).ToBeVisibleAsync();
        await Expect(Row(Section(page, "Клиенты"), "Петров")).ToBeVisibleAsync();
        await Expect(periods.Locator("[aria-current=page]")).ToHaveTextAsync("Сутки");

        await Filter(page).GetByRole(AriaRole.Link, new() { Name = "Все вызовы" }).ClickAsync();

        await Expect(Figure(page, "5 вызовов")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new Regex(@"metrics\?days=1$"));
    }

    [Fact]
    public async Task Removed_client_keeps_its_history_and_can_still_be_looked_into()
    {
        var (gateway, upstream) = await StartWithCallsAsync();
        await using var _ = upstream;
        await using var __ = gateway;
        await CliRunner.RunAsync(gateway.DataDir, "client", "remove", "Петров");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GotoAsync("metrics");

        await Expect(Figure(page, "5 вызовов")).ToBeVisibleAsync();
        await Expect(Row(Section(page, "Клиенты"), "удалён").GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("1");

        await Row(Section(page, "Клиенты"), "удалён").ClickAsync();

        await Expect(Filter(page)).ToContainTextAsync("Клиент удалён");
        await Expect(Figure(page, "1 вызов")).ToContainTextAsync("1 из них к инструментам");
        await Expect(Row(Section(page, "Инструменты"), "search")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Tool_name_sent_by_a_client_survives_the_address_and_is_never_run_as_markup()
    {
        const string hostile = "<img src=x onerror=document.title='hacked'> a&days=1#top";
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await CallAsync(gateway, "echo", issued.Token, ToolCall(hostile));
        await CallAsync(gateway, "echo", issued.Token, ToolCall("search"));
        await JournalHasAsync(gateway, 2);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        var periods = page.GetByRole(AriaRole.Navigation, new() { Name = "Период" });
        await page.GotoAsync("metrics");

        await Row(Section(page, "Инструменты"), hostile).ClickAsync();

        await Expect(Filter(page)).ToContainTextAsync("Инструмент " + hostile);
        await Expect(Figure(page, "1 вызов")).ToBeVisibleAsync();
        await Expect(periods.Locator("[aria-current=page]")).ToHaveTextAsync("7 дней");

        await periods.GetByRole(AriaRole.Link, new() { Name = "Сутки" }).ClickAsync();

        await Expect(periods.Locator("[aria-current=page]")).ToHaveTextAsync("Сутки");
        await Expect(Filter(page)).ToContainTextAsync("Инструмент " + hostile);
        await Expect(Figure(page, "1 вызов")).ToBeVisibleAsync();
        await Expect(page.Locator("img[src=x]")).ToHaveCountAsync(0);
        await Expect(page).ToHaveTitleAsync("Метрики · ctx-ui");
    }

    [Fact]
    public async Task Gateway_refusals_are_counted_apart_from_the_calls()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        for (var call = 0; call < 3; call++)
            await CallAsync(gateway, "echo", issued.Token, ToolCall("search"));
        await JournalHasAsync(gateway, 3);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GotoAsync("metrics");

        await Expect(Figure(page, "1 вызов")).ToContainTextAsync("1 из них к инструментам");
        await Expect(Figure(page, "0 ошибок")).ToBeVisibleAsync();
        await Expect(Section(page, "Отказы шлюза")).ToContainTextAsync("Квота исчерпана");
        await Expect(Row(Section(page, "Клиенты"), "Иванов").GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("2");
    }

    [Fact]
    public async Task Overview_clients_and_the_journal_link_into_the_metrics()
    {
        var (gateway, upstream) = await StartWithCallsAsync();
        await using var _ = upstream;
        await using var __ = gateway;
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await Row(page, "/mcp/broken").ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(@"metrics\?days=1&inbound="));
        await Expect(Filter(page)).ToContainTextAsync("Inbound broken");
        await Expect(Figure(page, "1 ошибка")).ToContainTextAsync("100 % вызовов");

        await page.GotoAsync("clients");
        await RowMenu.ChooseAsync(Row(page, "Петров"), "Метрики");

        await Expect(Filter(page)).ToContainTextAsync("Клиент Петров");
        await Filter(page).GetByRole(AriaRole.Link, new() { Name = "Журнал клиента" }).ClickAsync();

        await Expect(Rows(page)).ToHaveCountAsync(3);
        await Expect(Rows(page).Filter(new() { HasText = "Иванов" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Cut_off_link_opens_the_metrics_without_the_broken_part()
    {
        var (gateway, upstream) = await StartWithCallsAsync();
        await using var _ = upstream;
        await using var __ = gateway;
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GotoAsync("metrics?days=1&client=1f0c2a&inbound=");

        await Expect(Figure(page, "5 вызовов")).ToBeVisibleAsync();
        await Expect(Filter(page)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Период" }).Locator("[aria-current=page]")).ToHaveTextAsync("Сутки");
    }

    [Fact]
    public async Task Quiet_period_says_so()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await page.GotoAsync("metrics");

        await Expect(page.GetByText("За этот период вызовов не было")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Figure)).ToHaveCountAsync(0);
    }
}
