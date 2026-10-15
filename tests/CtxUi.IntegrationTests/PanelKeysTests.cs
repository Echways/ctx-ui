using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.ApiKeys;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelKeysTests
{
    private static async Task<IPage> OpenKeysAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Ключи API" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Ключи API" })).ToBeVisibleAsync();
        return session.Page;
    }

    private static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog);

    [Fact]
    public async Task Empty_list_says_what_a_key_is_for_and_links_to_the_docs()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenKeysAsync(session);

        await Expect(page).ToHaveTitleAsync("Ключи API");
        await Expect(page.GetByText("Ключей пока нет")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Документация API" }))
            .ToHaveAttributeAsync("href", "swagger/");
    }

    [Fact]
    public async Task Key_issued_in_the_panel_is_shown_once_and_opens_the_api()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenKeysAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Выпустить ключ" }).ClickAsync();
        await Dialog(page).GetByLabel("Название").FillAsync("bot");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Выпустить" }).ClickAsync();
        var key = await Dialog(page).GetByRole(AriaRole.Textbox, new() { Name = "Ключ", Exact = true }).InputValueAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Закрыть" }).ClickAsync();
        using var api = ApiClient.WithKey(gateway, key);
        using var response = await api.GetAsync("api/v1/inbounds");

        Assert.True(ApiKeySecret.IsWellFormed(key));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "bot" })).ToContainTextAsync(key[..11] + "…");
        Assert.DoesNotContain(key, await page.ContentAsync());
    }

    [Fact]
    public async Task Mistake_in_the_name_is_explained_in_place()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenKeysAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Выпустить ключ" }).ClickAsync();

        await Dialog(page).GetByLabel("Название").FillAsync("мой бот");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Выпустить" }).ClickAsync();

        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Название ключа «мой бот» недопустимо");
        await Expect(Dialog(page).GetByLabel("Название")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Revoked_key_stops_opening_the_api()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway, "bot");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenKeysAsync(session);

        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "bot" }).GetByRole(AriaRole.Button, new() { Name = "Отозвать" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Отозвать" }).ClickAsync();
        await Expect(page.GetByText("Ключей пока нет")).ToBeVisibleAsync();
        using var response = await api.GetAsync("api/v1/inbounds");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await gateway.ChangeAsync(services => services.GetRequiredService<ApiKeyService>().ListAsync()));
    }
}
