using System.Net;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientActionTests
{
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
    public async Task Notice_of_a_change_appears_at_the_bottom_left_without_an_icon()
    {
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Switch).UncheckAsync();

        var notice = page.GetByText("Клиент «Иванов» отключён");
        await Expect(notice).ToBeVisibleAsync();
        var box = (await notice.BoundingBoxAsync())!;
        Assert.True(box.X < 120, $"notice starts at x={box.X}");
        Assert.True(box.Y > page.ViewportSize!.Height / 2, $"notice starts at y={box.Y}");
        await Expect(page.Locator(".mud-snackbar svg")).ToHaveCountAsync(0);
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

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Сбросить квоту");
        await Expect(page.GetByText("Счётчик квоты клиента «Иванов» обнулён")).ToBeVisibleAsync();
        await Expect(Row(page, "Иванов")).ToContainTextAsync("0 / 1");
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

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Удалить");
        await Expect(Dialog(page)).ToContainTextAsync("Удалить клиента «Иванов»?");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
        using var kept = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Удалить");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Удалить" }).ClickAsync();
        await Expect(Row(page, "Иванов")).ToHaveCountAsync(0);
        using var gone = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        await Expect(Row(page, "Петров")).ToBeVisibleAsync();
    }
}
