using System.Net;
using Microsoft.AspNetCore.Localization;
using Microsoft.Playwright;
using CtxUi.Core;
using Cookie = Microsoft.Playwright.Cookie;

namespace CtxUi.IntegrationTests;

public sealed class PanelSession : IAsyncDisposable
{
    private const float PatienceMs = 5_000;

    private static readonly Lazy<Task<IBrowser>> Browser = new(LaunchAsync);

    private readonly IBrowserContext _context;

    private PanelSession(IBrowserContext context, IPage page, string password)
    {
        _context = context;
        Page = page;
        Password = password;
    }

    public IPage Page { get; }

    public string Password { get; }

    public static async Task<PanelSession> OpenAsync(GatewayHost gateway, string admin = "root", string? language = "ru")
    {
        var issued = await gateway.AddAdminAsync(admin);
        var context = await NewContextAsync(gateway, language);
        var page = await context.NewPageAsync();
        await page.GotoAsync("");
        return new PanelSession(context, page, issued.Password);
    }

    public static async Task<PanelSession> SignInAsync(GatewayHost gateway, string admin = "root")
    {
        var issued = await gateway.AddAdminAsync(admin);
        using var panel = new PanelClient(gateway);
        using (var signedIn = await panel.SignInAsync(admin, issued.Password))
        {
            if (signedIn.StatusCode != HttpStatusCode.Redirect)
                throw new InvalidOperationException($"sign-in answered {signedIn.StatusCode} instead of a redirect");
        }

        var context = await NewContextAsync(gateway, "ru");
        var session = panel.Cookies.GetCookies(new Uri(gateway.PanelUrl))
            .Where(cookie => cookie.Name.StartsWith(OwnCookies.Session, StringComparison.Ordinal));
        await context.AddCookiesAsync(session.Select(cookie => new Cookie
        {
            Name = cookie.Name,
            Value = cookie.Value,
            Domain = cookie.Domain,
            Path = cookie.Path,
            HttpOnly = cookie.HttpOnly,
            Secure = cookie.Secure,
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("");
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation)).ToBeVisibleAsync();
        return new PanelSession(context, page, issued.Password);
    }

    public async Task FillLoginAsync(string name, string password)
    {
        await Page.GetByLabel("Логин").FillAsync(name);
        await Page.GetByLabel("Пароль").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static async Task<IBrowserContext> NewContextAsync(GatewayHost gateway, string? language)
    {
        var browser = await Browser.Value;
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = gateway.PanelUrl,
            Locale = "ru-RU",
            Permissions = ["clipboard-read", "clipboard-write"],
        });
        context.SetDefaultTimeout(PatienceMs);
        if (language is not null)
        {
            await context.AddCookiesAsync([new Cookie
            {
                Name = CookieRequestCultureProvider.DefaultCookieName,
                Value = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(language)),
                Url = gateway.PanelUrl,
            }]);
        }

        return context;
    }

    private static async Task<IBrowser> LaunchAsync()
    {
        var installed = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (installed != 0)
            throw new InvalidOperationException($"Chromium для тестов панели не установился, код {installed}");

        Assertions.SetDefaultExpectTimeout(PatienceMs);
        var playwright = await Playwright.CreateAsync();
        return await playwright.Chromium.LaunchAsync();
    }
}
