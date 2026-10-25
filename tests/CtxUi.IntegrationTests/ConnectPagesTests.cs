using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class ConnectPagesTests
{
    private static async Task<IBrowserContext> BrowserAsync(GatewayHost gateway)
    {
        var browser = await PanelSession.SharedBrowserAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = gateway.BaseUrl,
            JavaScriptEnabled = false,
            ReducedMotion = ReducedMotion.Reduce,
            Locale = "ru-RU",
        });
        context.SetDefaultTimeout(5_000);
        return context;
    }

    private static async Task SignInAsync(IPage page, string key)
    {
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Вход" })).ToBeVisibleAsync();
        await page.GetByLabel("Ключ доступа").FillAsync(key);
        await page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
    }

    private static async Task<string> ApproveAsync(IPage page)
    {
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Разрешить доступ" })).ToBeVisibleAsync();
        var reached = page.WaitForRequestAsync(request => request.Url.StartsWith(OAuthFlow.Callback, StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Разрешить" }).ClickAsync();
        return (await reached).Url;
    }

    [Fact]
    public async Task Key_sign_in_and_consent_work_without_javascript()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();

        await page.GotoAsync(flow.AuthorizeUrl);
        await SignInAsync(page, issued.Token);
        await Expect(page.GetByText("Claude Code", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("название указало само приложение, оно не проверено")).ToBeVisibleAsync();
        var callback = await ApproveAsync(page);

        Assert.Contains("code=", callback);
    }

    [Fact]
    public async Task Sign_in_page_names_the_app_and_the_server()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();

        await page.GotoAsync(flow.AuthorizeUrl);

        await Expect(page.Locator("p.lead")).ToHaveTextAsync("Войдите, чтобы подключить Claude Code к echo.");
    }

    [Fact]
    public async Task Pages_follow_the_browser_language()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        var browser = await PanelSession.SharedBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = gateway.BaseUrl,
            JavaScriptEnabled = false,
            Locale = "en-US",
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync("/connect/signin");

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
    }

    [Fact]
    public async Task Wrong_key_stays_on_the_page_with_a_hint()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();

        await page.GotoAsync(flow.AuthorizeUrl);
        await SignInAsync(page, AccessToken.Generate());

        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Такой ключ не найден.");
        await Expect(page.GetByLabel("Ключ доступа")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Limit_page_replaces_a_device()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ArrangeAsync(services => services.GetRequiredService<ClientService>()
            .UpdateAsync(issued.Client.Id, new ClientUpdate { Access = new ClientAccessPolicy(1, true) }));
        using var laptop = await OAuthFlow.StartAsync(gateway, "echo");
        await laptop.SignInWithKeyAsync(issued.Token);
        await laptop.ApproveAsync();
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();

        await page.GotoAsync(flow.AuthorizeUrl);
        await SignInAsync(page, issued.Token);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Достигнут лимит устройств" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "и войти здесь" }).ClickAsync();
        var callback = await ApproveAsync(page);

        Assert.Contains("code=", callback);
    }

    [Fact]
    public async Task Devices_page_lists_this_device_and_signs_out()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", "https://mcp.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();
        await page.GotoAsync(flow.AuthorizeUrl);
        await SignInAsync(page, issued.Token);
        await ApproveAsync(page);

        var devices = await browser.NewPageAsync();
        await devices.GotoAsync("/connect/devices");
        await Expect(devices.GetByRole(AriaRole.Heading, new() { Name = "Мои устройства" })).ToBeVisibleAsync();
        await Expect(devices.GetByText("Это устройство")).ToBeVisibleAsync();
        await Expect(devices.GetByText("Claude Code")).ToBeVisibleAsync();
        await devices.GetByRole(AriaRole.Button, new() { Name = "Выйти" }).ClickAsync();

        await Expect(devices.GetByRole(AriaRole.Heading, new() { Name = "Вход" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Language_switches_to_english_and_back()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();
        await page.GotoAsync("/connect/signin");

        await page.GetByRole(AriaRole.Link, new() { Name = "English" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await page.GetByRole(AriaRole.Link, new() { Name = "Русский" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Вход" })).ToBeVisibleAsync();
        Assert.EndsWith("/connect/signin", page.Url);
    }

    [Fact]
    public async Task Pages_forbid_frames_and_load_nothing_external()
    {
        await using var gateway = await OAuthHarness.StartAsync();
        await using var browser = await BrowserAsync(gateway);
        var page = await browser.NewPageAsync();
        var requests = new List<string>();
        page.Request += (_, request) => requests.Add(request.Url);

        var response = await page.GotoAsync("/connect/signin");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var headers = await response!.AllHeadersAsync();
        Assert.Contains("frame-ancestors 'none'", headers["content-security-policy"]);
        Assert.Equal("DENY", headers["x-frame-options"]);
        Assert.All(requests, url => Assert.StartsWith(gateway.BaseUrl, url));
        Assert.Contains(requests, url => url.EndsWith("/_content/CtxUi.Connect/connect.css", StringComparison.Ordinal));
        Assert.Contains(requests, url => url.EndsWith("onest.woff2", StringComparison.Ordinal));
    }
}
