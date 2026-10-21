using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientsTests
{
    [Fact]
    public async Task Empty_list_says_how_access_is_given()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenClientsAsync(session);

        await Expect(page).ToHaveTitleAsync("Клиенты · ctx-ui");
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
        var disabled = await gateway.AddClientAsync("Отключённый", ["echo"]);
        await gateway.AddClientAsync("Истёкший", ["echo"]);
        var spender = await gateway.AddClientAsync("Исчерпавший", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().SetEnabledAsync(disabled.Client.Id, false);
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
        await Expect(Row(page, "Активный")).ToContainTextAsync("0 / 100");
        await Expect(Row(page, "Активный")).ToContainTextAsync("docs, echo");
        await Expect(Row(page, "Активный").GetByRole(AriaRole.Switch)).ToBeCheckedAsync();
        await Expect(Row(page, "Отключённый")).ToContainTextAsync("отключён");
        await Expect(Row(page, "Отключённый")).ToContainTextAsync("бессрочно");
        await Expect(Row(page, "Отключённый").GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("–");
        await Expect(Row(page, "Отключённый").GetByRole(AriaRole.Switch)).Not.ToBeCheckedAsync();
        await Expect(Row(page, "Истёкший")).ToContainTextAsync("истёк");
        await Expect(Row(page, "Истёкший")).ToContainTextAsync("до 2020-02-03");
        await Expect(Row(page, "Исчерпавший")).ToContainTextAsync("исчерпан");
        await Expect(Row(page, "Исчерпавший")).ToContainTextAsync("1 / 1");
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
        await Expect(page.GetByText("Никого с именем «никого»")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Сбросить поиск" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(4);
        await Expect(page.GetByLabel("Поиск по имени")).ToHaveValueAsync("");
    }
}
