using System.Collections;
using System.Globalization;
using System.Net;
using System.Resources;
using Microsoft.Playwright;
using CtxUi.Core;
using CtxUi.Panel;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelLanguageTests
{
    [Fact]
    public async Task Panel_speaks_english_until_russian_is_chosen()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.OpenAsync(gateway, language: null);
        var page = session.Page;
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in" })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Русский" }).ClickAsync();

        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "ru");
        await session.FillLoginAsync("root", session.Password);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Обзор" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Switching_language_keeps_the_open_page()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" }).ClickAsync();

        await page.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Button, new() { Name = "English" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(gateway.PanelUrl + "clients");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Clients" })).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");

        await page.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Button, new() { Name = "Русский" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Клиенты" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Domain_errors_follow_the_panel_language()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = session.Page;
        await page.GetByRole(AriaRole.Banner).GetByRole(AriaRole.Button, new() { Name = "English" }).ClickAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Inbounds" }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Add inbound" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Alert)).ToContainTextAsync("is not allowed");
    }

    [Fact]
    public async Task Unknown_language_does_not_exist()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var response = await panel.GetAsync("language/de");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("//evil.example/", "/evil.example/")]
    [InlineData("/\\evil.example", "/evil.example")]
    [InlineData("clients?from=menu", "/clients?from=menu")]
    public async Task Language_switch_returns_only_inside_the_panel(string back, string expected)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var response = await panel.GetAsync($"language/en?back={Uri.EscapeDataString(back)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(panel.Root + expected, response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(typeof(PanelServices), "CtxUi.Panel.PanelText")]
    [InlineData(typeof(DomainException), "CtxUi.Core.CoreText")]
    public void Every_text_has_both_languages(Type anchor, string resources)
    {
        var manager = new ResourceManager(resources, anchor.Assembly);

        Assert.Equal(Keys(manager, CultureInfo.InvariantCulture), Keys(manager, CultureInfo.GetCultureInfo("ru")));
    }

    private static List<string> Keys(ResourceManager manager, CultureInfo culture) =>
        manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
}
