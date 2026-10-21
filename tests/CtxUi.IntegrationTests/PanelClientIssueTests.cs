using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientIssueTests
{
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
}
