using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientEditTests
{
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
    public async Task Clicking_a_row_opens_the_client_for_editing()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Cell).First.ClickAsync();

        await Expect(Dialog(page)).ToContainTextAsync("Клиент «Иванов»");
    }
}
