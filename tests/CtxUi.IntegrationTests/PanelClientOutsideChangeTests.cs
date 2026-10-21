using Microsoft.Playwright;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientOutsideChangeTests
{
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

        await Expect(Row(page, "Петров")).ToContainTextAsync("0 / 7");
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

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Сбросить квоту");

        await Expect(page.GetByText("Клиент «Иванов» не найден")).ToBeVisibleAsync();
        await Expect(page.GetByText("Клиентов пока нет")).ToBeVisibleAsync();
    }
}
