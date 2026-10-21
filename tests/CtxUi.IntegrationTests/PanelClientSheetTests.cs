using Microsoft.Playwright;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientSheetTests
{
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
        await Field(page, "Имя").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(Dialog(page)).ToHaveCountAsync(0);
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
    public async Task Form_cannot_issue_a_client_while_there_is_no_inbound()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();

        await Expect(Dialog(page)).ToContainTextAsync("Сначала добавьте inbound");
        await Expect(Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" })).ToBeDisabledAsync();
    }
}
