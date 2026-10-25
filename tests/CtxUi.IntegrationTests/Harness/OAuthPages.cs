using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.SignIn;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

internal static class OAuthPages
{
    public const string PublicUrl = "https://mcp.example.org";

    public static Task<GatewayHost> StartAsync(string publicUrl = PublicUrl) => GatewayHost.StartAsync(builder =>
    {
        builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00";
        builder.Configuration["CtxUi:PublicUrl"] = publicUrl;
    });

    public static async Task<IPage> OpenAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "OAuth", Exact = true }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "OAuth", Exact = true })).ToBeVisibleAsync();
        return session.Page;
    }

    public static ILocator Switch(IPage page, string name) => page.GetByRole(AriaRole.Switch, new() { Name = name, Exact = true });

    public static async Task<ILocator> OpenCardAsync(IPage page, string title)
    {
        var card = page.Locator("details.provider").Filter(new() { HasText = title });
        if (await card.GetAttributeAsync("open") is null)
            await card.Locator("summary").ClickAsync();

        return card;
    }

    public static Task EnableAsync(GatewayHost gateway, string publicUrl = PublicUrl) => gateway.ArrangeAsync(services =>
        services.GetRequiredService<SignInSettingsService>().UpdateAsync(new SignInSettingsUpdate { Enabled = true }, publicUrl));

    public static Task AddProviderAsync(GatewayHost gateway, string name = "github") => gateway.ArrangeAsync(services =>
        services.GetRequiredService<SignInSettingsService>().SetProviderAsync(new ProviderSetup(name, "client-id", "secret")));
}
