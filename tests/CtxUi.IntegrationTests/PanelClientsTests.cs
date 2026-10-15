using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientsTests
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private static Task<GatewayHost> StartWithoutPollingAsync() =>
        GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");

    private static async Task<IPage> OpenClientsAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Клиенты" })).ToBeVisibleAsync();
        return session.Page;
    }

    private static ILocator Row(IPage page, string name) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = name });

    private static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog);

    private static async Task<bool> IsQuotaRefusalAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Contains("Лимит вызовов исчерпан");

    [Fact]
    public async Task Empty_list_says_how_access_is_given()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenClientsAsync(session);

        await Expect(page).ToHaveTitleAsync("Клиенты");
        await Expect(page.GetByText("Клиентов пока нет")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Table)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task List_shows_state_term_last_use_and_spent_quota_of_every_client()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        await gateway.AddClientAsync(
            "Активный", ["echo", "docs"], new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), limits: new ClientLimits(QuotaCalls: 100));
        await gateway.AddClientAsync("Отключённый", ["echo"]);
        await gateway.AddClientAsync("Истёкший", ["echo"]);
        var spender = await gateway.AddClientAsync("Исчерпавший", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().SetEnabledAsync("Отключённый", false);
            var db = services.GetRequiredService<CtxUiDbContext>();
            (await db.Clients.SingleAsync(client => client.Name == "Истёкший")).ExpiresAt = new DateTimeOffset(2020, 2, 3, 0, 0, 0, TimeSpan.Zero);
            return await db.SaveChangesAsync();
        });
        using var spent = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", spender.Token, ToolCall));
        await Eventually.TrueAsync(
            () => gateway.ChangeAsync(async services =>
                (await services.GetRequiredService<ClientService>().GetAsync("Исчерпавший")) is { UsedCalls: 1, LastUsedAt: not null }),
            "счётчик и отметка обращения дошли до базы");
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenClientsAsync(session);

        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(5);
        await Expect(Row(page, "Активный")).ToContainTextAsync("активен");
        await Expect(Row(page, "Активный")).ToContainTextAsync("до 2099-01-01");
        await Expect(Row(page, "Активный")).ToContainTextAsync("не обращался");
        await Expect(Row(page, "Активный")).ToContainTextAsync("0/100");
        await Expect(Row(page, "Активный")).ToContainTextAsync("docs, echo");
        await Expect(Row(page, "Активный").GetByRole(AriaRole.Switch)).ToBeCheckedAsync();
        await Expect(Row(page, "Отключённый")).ToContainTextAsync("отключён");
        await Expect(Row(page, "Отключённый")).ToContainTextAsync("бессрочно");
        await Expect(Row(page, "Отключённый")).ToContainTextAsync("0/∞");
        await Expect(Row(page, "Отключённый").GetByRole(AriaRole.Switch)).Not.ToBeCheckedAsync();
        await Expect(Row(page, "Истёкший")).ToContainTextAsync("истёк");
        await Expect(Row(page, "Истёкший")).ToContainTextAsync("до 2020-02-03");
        await Expect(Row(page, "Исчерпавший")).ToContainTextAsync("исчерпан");
        await Expect(Row(page, "Исчерпавший")).ToContainTextAsync("1/1");
        await Expect(Row(page, "Исчерпавший")).Not.ToContainTextAsync("не обращался");
    }

    [Fact]
    public async Task Search_leaves_the_clients_whose_name_has_the_typed_letters()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Иванов 4419", ["echo"]);
        await gateway.AddClientAsync("Петров 4419", ["echo"]);
        await gateway.AddClientAsync("Сидорова 5520", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await page.GetByLabel("Поиск по имени").FillAsync("иван");
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(2);
        await Expect(Row(page, "Иванов 4419")).ToBeVisibleAsync();

        await page.GetByLabel("Поиск по имени").FillAsync("4419");
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(3);

        await page.GetByLabel("Поиск по имени").FillAsync("никого");
        await Expect(page.GetByText("Никто не найден")).ToBeVisibleAsync();

        await page.GetByLabel("Поиск по имени").FillAsync("");
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(4);
    }

    [Fact]
    public async Task Switch_closes_and_returns_access_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Switch).UncheckAsync();
        await Expect(page.GetByText("Клиент «Иванов» отключён")).ToBeVisibleAsync();
        await Expect(Row(page, "Иванов")).ToContainTextAsync("отключён");
        using var closed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await Row(page, "Иванов").GetByRole(AriaRole.Switch).CheckAsync();
        await Expect(page.GetByText("Клиент «Иванов» включён")).ToBeVisibleAsync();
        using var open = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task Quota_reset_gives_the_calls_back_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Сбросить квоту" }).ClickAsync();
        await Expect(page.GetByText("Счётчик квоты клиента «Иванов» обнулён")).ToBeVisibleAsync();
        await Expect(Row(page, "Иванов")).ToContainTextAsync("0/1");
        using var served = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));

        Assert.True(await IsQuotaRefusalAsync(refused));
        Assert.False(await IsQuotaRefusalAsync(served));
        Assert.Equal(2, upstream.Requests.Count);
    }

    [Fact]
    public async Task Client_is_removed_only_after_the_admin_confirms()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.AddClientAsync("Петров", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Удалить" }).ClickAsync();
        await Expect(Dialog(page)).ToContainTextAsync("Удалить клиента «Иванов»?");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
        using var kept = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Удалить" }).ClickAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Удалить" }).ClickAsync();
        await Expect(Row(page, "Иванов")).ToHaveCountAsync(0);
        using var gone = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        await Expect(Row(page, "Петров")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Refresh_button_shows_what_changed_outside_the_panel()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);
        await CliRunner.RunAsync(gateway.DataDir, "client", "add", "Петров", "--inbound", "echo", "--quota", "7");

        await page.GetByRole(AriaRole.Button, new() { Name = "Обновить" }).ClickAsync();

        await Expect(Row(page, "Петров")).ToContainTextAsync("0/7");
    }

    [Fact]
    public async Task Client_removed_from_the_command_line_meanwhile_is_reported_not_crashed_on()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);
        await CliRunner.RunAsync(gateway.DataDir, "client", "remove", "Иванов");

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Сбросить квоту" }).ClickAsync();

        await Expect(page.GetByText("Клиент «Иванов» не найден")).ToBeVisibleAsync();
        await Expect(page.GetByText("Клиентов пока нет")).ToBeVisibleAsync();
    }
}
