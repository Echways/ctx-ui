using System.Net;
using Microsoft.Playwright;
using static CtxUi.IntegrationTests.InboundPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelInboundsTests
{
    [Fact]
    public async Task Empty_list_says_what_an_inbound_is()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenInboundsAsync(session);

        await Expect(page).ToHaveTitleAsync("Inbound'ы · ctx-ui");
        await Expect(page.GetByText("Inbound'ов пока нет")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Table)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Switch_closes_and_reopens_the_inbound_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await Row(page, "echo").GetByRole(AriaRole.Switch).UncheckAsync();
        await Expect(page.GetByText("Inbound «echo» выключен")).ToBeVisibleAsync();
        using var closed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await Row(page, "echo").GetByRole(AriaRole.Switch).CheckAsync();
        await Expect(page.GetByText("Inbound «echo» включён")).ToBeVisibleAsync();
        using var open = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task Inbound_is_removed_only_after_the_admin_confirms()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo", "docs"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "echo"), "Удалить");
        await Expect(Dialog(page)).ToContainTextAsync("Клиентов с доступом к нему: 1");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
        await Expect(Row(page, "echo")).ToBeVisibleAsync();
        using var kept = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await RowMenu.ChooseAsync(Row(page, "echo"), "Удалить");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Удалить" }).ClickAsync();
        await Expect(Row(page, "echo")).ToHaveCountAsync(0);
        using var gone = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var other = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/docs", issued.Token));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await Expect(Row(page, "docs")).ToContainTextAsync("1");
    }

    [Fact]
    public async Task Inbound_removed_from_the_command_line_meanwhile_is_reported_not_crashed_on()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);
        await CliRunner.RunAsync(gateway.DataDir, "inbound", "remove", "echo");

        await Row(page, "echo").GetByRole(AriaRole.Switch).ClickAsync();

        await Expect(page.GetByText("Inbound «echo» не найден")).ToBeVisibleAsync();
        await Expect(Row(page, "echo")).ToHaveCountAsync(0);
    }
}
