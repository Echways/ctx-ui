using Microsoft.Playwright;
using CtxUi.Core.Inbounds;
using static CtxUi.IntegrationTests.InboundPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelInboundFormTests
{
    [Fact]
    public async Task Inbound_added_through_the_form_is_listed_and_saved_with_sealed_headers()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Dialog(page).GetByLabel("Slug").FillAsync("learn");
        await Dialog(page).GetByLabel("Название").FillAsync("Учебный");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync("https://learn.example.org/api/mcp");
        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("Authorization=Bearer s3cret\nX-Tenant=7");
        await Dialog(page).GetByLabel("Передавать upstream личность клиента").CheckAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Row(page, "learn")).ToContainTextAsync("Учебный");
        await Expect(Row(page, "learn")).ToContainTextAsync("https://learn.example.org/api/mcp");
        await Expect(Row(page, "learn").GetByRole(AriaRole.Switch)).ToBeCheckedAsync();
        var stored = await StoredAsync(gateway, "learn");
        Assert.Equal("Учебный", stored.Name);
        Assert.True(stored.ForwardIdentity);
        Assert.Equal(Inbound.DefaultRequestTimeoutSeconds, stored.RequestTimeoutSeconds);
        Assert.Equal(Inbound.DefaultMaxBodyBytes, stored.MaxBodyBytes);
        Assert.DoesNotContain("s3cret", stored.UpstreamHeadersSealed);
        Assert.DoesNotContain("s3cret", await page.ContentAsync());
    }

    [Fact]
    public async Task Mistake_in_the_form_is_explained_in_place_and_can_be_corrected()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();

        await Dialog(page).GetByLabel("Slug").FillAsync("Мой сервер");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync("https://learn.example.org/mcp");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Slug «Мой сервер» недопустим");

        await Dialog(page).GetByLabel("Slug").FillAsync("echo");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Inbound «echo» уже есть");

        await Dialog(page).GetByLabel("Slug").FillAsync("learn");
        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("без знака равно");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("ключ=значение");

        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Row(page, "learn")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task Cancelled_form_changes_nothing()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Dialog(page).GetByLabel("Slug").FillAsync("learn");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync("https://learn.example.org/mcp");

        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByText("Inbound'ов пока нет")).ToBeVisibleAsync();
    }
}
