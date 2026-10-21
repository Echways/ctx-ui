using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelSessionTests
{
    [Fact]
    public async Task Open_page_is_closed_when_the_admin_gets_a_new_password()
    {
        await using var gateway = await GatewayHost.StartAsync(
            builder => builder.Configuration["CtxUi:SessionCheckInterval"] = "00:00:00.200");
        await using var session = await PanelSession.SignInAsync(gateway);

        await CliRunner.RunAsync(gateway.DataDir, "admin", "password", "root");

        await Expect(session.Page.GetByLabel("Пароль")).ToBeVisibleAsync();
        await session.FillLoginAsync("root", session.Password);
        await Expect(session.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Неверный логин или пароль");
    }

    [Fact]
    public async Task Open_page_is_closed_when_the_admin_is_removed()
    {
        await using var gateway = await GatewayHost.StartAsync(
            builder => builder.Configuration["CtxUi:SessionCheckInterval"] = "00:00:00.200");
        await using var session = await PanelSession.SignInAsync(gateway);

        await CliRunner.RunAsync(gateway.DataDir, "admin", "remove", "root");

        await Expect(session.Page.GetByLabel("Пароль")).ToBeVisibleAsync();
    }
}
