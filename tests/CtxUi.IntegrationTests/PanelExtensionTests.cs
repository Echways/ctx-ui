using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelExtensionTests
{
    private static async Task<string[]> MenuOfAsync(IPage page, string client)
    {
        await ClientPages.Row(page, client).GetByRole(AriaRole.Button, new() { Name = "Ещё действия" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Menuitem).First).ToBeVisibleAsync();
        return [.. (await page.GetByRole(AriaRole.Menuitem).AllInnerTextsAsync()).Select(text => text.Trim())];
    }

    [Fact]
    public async Task Without_extensions_the_client_menu_and_navigation_stay_as_they_are()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "http://127.0.0.1:1/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await ClientPages.OpenClientsAsync(session);

        var links = await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link).AllInnerTextsAsync();
        Assert.Equal(
            ["Обзор", "Метрики", "Inbound'ы", "Клиенты", "Журнал", "OAuth", "Ключи API"],
            links.Select(link => link.Trim()).Where(link => link.Length > 0 && link != "ctx-ui"));
        Assert.Equal(
            ["Изменить", "Метрики", "Устройства", "Сбросить квоту", "Новый токен", "Удалить"],
            await MenuOfAsync(page, "Иванов"));
    }
}
