using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Usage;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelOverviewTests
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private static ILocator Figure(IPage page, string name) => page.GetByRole(AriaRole.Figure, new() { Name = name });

    private static ILocator Horizon(IPage page) => page.GetByRole(AriaRole.Img, new() { Name = "Вызовы за последние сутки" });

    private static ILocator Row(IPage page, string text) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = text });

    private static Task JournalHasAsync(GatewayHost gateway, int calls) => Eventually.TrueAsync(
        () => gateway.ChangeAsync(async services =>
            (await services.GetRequiredService<UsageStore>().RecentAsync(null, 100)).Count == calls),
        "журнал дошёл до базы");

    [Fact]
    public async Task Fresh_installation_shows_zeroes_and_leads_to_the_first_step()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await Expect(Horizon(page)).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("всего 0"));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Шлюз ещё ничего не раздаёт" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Figure)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Table)).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Link, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Новый inbound");
    }

    [Fact]
    public async Task Numbers_tell_about_clients_inbounds_and_the_calls_of_the_last_day()
    {
        await using var upstream = await FakeUpstream.StartAsync((app, _) =>
            app.Map("/broken", context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("broken", upstream.BaseUrl + "/broken");
        await gateway.AddInboundAsync("idle", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo", "broken"]);
        var disabled = await gateway.AddClientAsync("Отключённый", ["echo"]);
        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<InboundService>().SetEnabledAsync("idle", false);
            await services.GetRequiredService<ClientService>().SetEnabledAsync(disabled.Client.Id, false);
            return true;
        });
        using var first = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var second = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var listed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolsList));
        using var failed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/broken", issued.Token, ToolCall));
        await JournalHasAsync(gateway, 4);
        await Eventually.TrueAsync(
            () => gateway.ChangeAsync(async services =>
                (await services.GetRequiredService<ClientService>().GetAsync("Иванов")).LastUsedAt is not null),
            "отметка обращения дошла до базы");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await Expect(Figure(page, "1 из 2 клиентов")).ToContainTextAsync("активен сейчас");
        await Expect(Figure(page, "1 клиент заходил")).ToBeVisibleAsync();
        await Expect(Figure(page, "4 вызова за сутки")).ToContainTextAsync("3 из них к инструментам");
        await Expect(Figure(page, "1 сбой upstream")).ToContainTextAsync("25 % вызовов");
        await Expect(Horizon(page)).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("всего 4, сбоев 1"));
        await Expect(Row(page, "/mcp/echo").GetByRole(AriaRole.Cell)).ToHaveTextAsync(["/mcp/echo", "echo", "2", "3", "–"]);
        await Expect(Row(page, "/mcp/broken").GetByRole(AriaRole.Cell)).ToHaveTextAsync(["/mcp/broken", "broken", "1", "1", "1"]);
        await Expect(Row(page, "/mcp/idle").GetByRole(AriaRole.Cell)).ToHaveTextAsync(["/mcp/idle", "idle выключен", "–", "–", "–"]);
        await page.GetByText("Подробно по дням").ClickAsync();
        var today = DateTimeOffset.UtcNow.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru"));
        await Expect(Row(page, today).GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("4");
        await Expect(Row(page, today).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("3");
        await Expect(Row(page, today).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("1");
        await Expect(Row(page, today).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("–");
    }

    [Fact]
    public async Task Refresh_button_brings_the_calls_made_since_the_page_was_opened()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        await Expect(Figure(page, "0 вызовов за сутки")).ToBeVisibleAsync();
        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await JournalHasAsync(gateway, 1);

        await page.GetByRole(AriaRole.Button, new() { Name = "Обновить" }).ClickAsync();

        await Expect(Figure(page, "1 вызов за сутки")).ToContainTextAsync("1 из них к инструментам");
    }

    [Fact]
    public async Task Horizon_reads_out_the_quarter_chosen_with_the_arrow_keys()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var first = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var second = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await JournalHasAsync(gateway, 2);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;

        await Horizon(page).FocusAsync();
        await page.Keyboard.PressAsync("ArrowLeft");

        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("UTC · 2 вызова");
        await page.Keyboard.PressAsync("ArrowLeft");
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("UTC · ни одного вызова");
    }
}
