using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelSignInFormTests
{
    [Fact]
    public async Task Admin_signs_in_through_the_form_and_lands_on_the_overview()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.OpenAsync(gateway);
        var page = session.Page;
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));

        await session.FillLoginAsync("root", session.Password);

        await Expect(page).ToHaveURLAsync(gateway.PanelUrl);
        await Expect(page).ToHaveTitleAsync("Обзор · ctx-ui");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Обзор" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Обзор" }))
            .ToHaveAttributeAsync("aria-current", "page");
        await Expect(page.GetByRole(AriaRole.Banner)).ToContainTextAsync("root");
    }

    [Fact]
    public async Task Wrong_password_keeps_the_visitor_at_the_form()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.OpenAsync(gateway);

        await session.FillLoginAsync("root", "not-the-password-1234");

        await Expect(session.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Неверный логин или пароль");
        await Expect(session.Page.GetByLabel("Логин")).ToHaveValueAsync("root");
        await Expect(session.Page.GetByRole(AriaRole.Navigation)).ToHaveCountAsync(0);
    }
}
