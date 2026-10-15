using Microsoft.Playwright;

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

    public static async Task<PanelSession> OpenAsync(GatewayHost gateway, string admin = "root")
    {
        var issued = await gateway.AddAdminAsync(admin);
        var browser = await Browser.Value;
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = gateway.PanelUrl,
            Locale = "ru-RU",
            Permissions = ["clipboard-read", "clipboard-write"],
        });
        context.SetDefaultTimeout(PatienceMs);
        var page = await context.NewPageAsync();
        await page.GotoAsync("");
        return new PanelSession(context, page, issued.Password);
    }

    public static async Task<PanelSession> SignInAsync(GatewayHost gateway, string admin = "root")
    {
        var session = await OpenAsync(gateway, admin);
        await session.FillLoginAsync(admin, session.Password);
        await Assertions.Expect(session.Page.GetByRole(AriaRole.Navigation)).ToBeVisibleAsync();
        return session;
    }

    public async Task FillLoginAsync(string name, string password)
    {
        await Page.GetByLabel("Логин").FillAsync(name);
        await Page.GetByLabel("Пароль").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Войти" }).ClickAsync();
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

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
