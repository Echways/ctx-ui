using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientFormTests
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

    private static ILocator Field(IPage page, string label) =>
        Dialog(page).GetByRole(AriaRole.Textbox, new() { Name = label, Exact = true });

    private static Task SaveAsync(IPage page) => Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();

    private static Task AddAsync(IPage page) => Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();

    private static Task<Client> StoredAsync(GatewayHost gateway, string name) =>
        gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync(name));

    [Fact]
    public async Task Client_added_through_the_form_gets_a_token_that_works_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();
        await Field(page, "Имя").FillAsync("Иванов 4419");
        await Dialog(page).GetByLabel("echo").CheckAsync();
        await AddAsync(page);

        await Expect(Dialog(page)).ToContainTextAsync("Подключение: Иванов 4419");
        var token = (await Dialog(page).GetByLabel("Токен", new() { Exact = true }).TextContentAsync())!;
        using var allowed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", token));
        using var other = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/docs", token));
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Закрыть" }).ClickAsync();

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        await Expect(Row(page, "Иванов 4419")).ToContainTextAsync("активен");
        await Expect(Row(page, "Иванов 4419")).ToContainTextAsync("бессрочно");
        await Expect(Row(page, "Иванов 4419").GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("–");
    }

    [Fact]
    public async Task Term_limits_and_metadata_typed_into_the_form_are_what_gets_stored()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddInboundAsync("docs", "https://docs.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();
        await Field(page, "Имя").FillAsync("Иванов");
        await Dialog(page).GetByLabel("echo").CheckAsync();
        await Dialog(page).GetByLabel("docs").CheckAsync();
        await Dialog(page).GetByLabel("Срок действия").FillAsync("2099-12-31");
        await Dialog(page).GetByLabel("Запросов в минуту").FillAsync("60");
        await Dialog(page).GetByLabel("Одновременных запросов").FillAsync("2");
        await Dialog(page).GetByLabel("Квота вызовов инструментов").FillAsync("500");
        await Dialog(page).GetByLabel("Обновлять квоту каждые, дней").FillAsync("30");
        await Dialog(page).GetByLabel("Метаданные").FillAsync("group=4419\nstorage-mb=50");
        await AddAsync(page);
        await Expect(Dialog(page)).ToContainTextAsync("Подключение: Иванов");

        var stored = await StoredAsync(gateway, "Иванов");
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero), stored.ExpiresAt);
        Assert.Equal(new ClientLimits(60, 2, 500, 30), ClientLimits.Of(stored));
        Assert.Equal(["docs", "echo"], stored.Inbounds.Select(link => link.Inbound!.Slug).Order());
        Assert.Equal("4419", stored.Metadata["group"]);
        Assert.Equal("50", stored.Metadata["storage-mb"]);
        Assert.Contains(
            "Доступ действует до 2099-12-31 (UTC).\nВызовов инструментов: 500 каждые 30 дн.",
            await Dialog(page).GetByLabel("Сообщение для клиента", new() { Exact = true }).TextContentAsync());
    }

    [Fact]
    public async Task Mistakes_are_explained_in_place_and_nobody_is_added_until_they_are_fixed()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Петров", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();

        await Field(page, "Имя").FillAsync("Иванов");
        await AddAsync(page);
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Укажите хотя бы один inbound");

        await Dialog(page).GetByLabel("echo").CheckAsync();
        await Field(page, "Имя").FillAsync("петров");
        await AddAsync(page);
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Клиент «петров» уже есть");

        await Field(page, "Имя").FillAsync("Иванов");
        await Dialog(page).GetByLabel("Срок действия").FillAsync("2020-01-01");
        await AddAsync(page);
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Срок 2020-01-01 уже прошёл");

        await Dialog(page).GetByLabel("Срок действия").FillAsync("");
        await Dialog(page).GetByLabel("Метаданные").FillAsync("просто слово");
        await AddAsync(page);
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("ключ=значение");
        Assert.Single(await gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().ListAsync()));

        await Dialog(page).GetByLabel("Метаданные").FillAsync("");
        await AddAsync(page);
        await Expect(Dialog(page)).ToContainTextAsync("Подключение: Иванов");
    }

    [Fact]
    public async Task Form_cannot_issue_a_client_while_there_is_no_inbound()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();

        await Expect(Dialog(page)).ToContainTextAsync("Сначала добавьте inbound");
        await Expect(Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" })).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Edit_form_opens_with_what_is_stored()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddInboundAsync("docs", "https://docs.example.org/mcp");
        await gateway.AddClientAsync(
            "Иванов",
            ["docs"],
            new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero),
            [KeyValuePair.Create("group", "4419")],
            new ClientLimits(60, 2, 500, 30));
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Изменить");

        await Expect(Field(page, "Имя")).ToHaveValueAsync("Иванов");
        await Expect(Field(page, "Имя")).ToBeDisabledAsync();
        await Expect(Dialog(page).GetByLabel("docs")).ToBeCheckedAsync();
        await Expect(Dialog(page).GetByLabel("echo")).Not.ToBeCheckedAsync();
        await Expect(Dialog(page).GetByLabel("Срок действия")).ToHaveValueAsync("2099-12-31");
        await Expect(Dialog(page).GetByLabel("Запросов в минуту")).ToHaveValueAsync("60");
        await Expect(Dialog(page).GetByLabel("Одновременных запросов")).ToHaveValueAsync("2");
        await Expect(Dialog(page).GetByLabel("Квота вызовов инструментов")).ToHaveValueAsync("500");
        await Expect(Dialog(page).GetByLabel("Обновлять квоту каждые, дней")).ToHaveValueAsync("30");
        await Expect(Dialog(page).GetByLabel("Метаданные")).ToHaveValueAsync("group=4419");
    }

    [Fact]
    public async Task Edited_access_and_quota_apply_to_the_very_next_request()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], metadata: [KeyValuePair.Create("group", "4419")]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Изменить");
        await Dialog(page).GetByLabel("echo").UncheckAsync();
        await Dialog(page).GetByLabel("docs").CheckAsync();
        await Dialog(page).GetByLabel("Квота вызовов инструментов").FillAsync("1");
        await Dialog(page).GetByLabel("Метаданные").FillAsync("");
        await SaveAsync(page);
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Row(page, "Иванов")).ToContainTextAsync("0 / 1");
        await Expect(Row(page, "Иванов")).ToContainTextAsync("docs");

        using var closed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var served = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/docs", issued.Token, ToolCall));
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/docs", issued.Token, ToolCall));

        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Contains("Лимит вызовов исчерпан", await refused.Content.ReadAsStringAsync());
        Assert.Empty((await StoredAsync(gateway, "Иванов")).Metadata);
    }

    [Fact]
    public async Task Expired_client_can_be_edited_without_touching_the_date_and_revived_by_clearing_it()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ArrangeAsync(async services =>
        {
            var db = services.GetRequiredService<CtxUiDbContext>();
            (await db.Clients.SingleAsync()).ExpiresAt = new DateTimeOffset(2020, 2, 3, 0, 0, 0, TimeSpan.Zero);
            return await db.SaveChangesAsync();
        });
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Изменить");
        await Dialog(page).GetByLabel("Запросов в минуту").FillAsync("10");
        await SaveAsync(page);
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Row(page, "Иванов")).ToContainTextAsync("истёк");
        Assert.Equal(10, (await StoredAsync(gateway, "Иванов")).RatePerMinute);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Изменить");
        await Dialog(page).GetByLabel("Срок действия").FillAsync("");
        await SaveAsync(page);
        await Expect(Row(page, "Иванов")).ToContainTextAsync("активен");
        await Expect(Row(page, "Иванов")).ToContainTextAsync("бессрочно");
    }

    [Fact]
    public async Task Cancelled_form_changes_nothing()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();
        await Field(page, "Имя").FillAsync("Иванов");
        await Dialog(page).GetByLabel("echo").CheckAsync();

        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByText("Клиентов пока нет")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Sheet_closes_with_the_close_word_and_with_escape()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Закрыть" }).ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();
        await Expect(Field(page, "Имя")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(Dialog(page)).ToHaveCountAsync(0);
    }
}
