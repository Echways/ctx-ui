using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Inbounds;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

internal static class InboundPages
{
    public static Task<GatewayHost> StartWithoutPollingAsync() =>
        GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");

    public static async Task<IPage> OpenInboundsAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Inbound'ы" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Inbound'ы" })).ToBeVisibleAsync();
        return session.Page;
    }

    public static ILocator Row(IPage page, string slug) =>
        page.GetByRole(AriaRole.Row).Filter(new() { HasText = $"/mcp/{slug}" });

    public static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog);

    public static Task<Inbound> StoredAsync(GatewayHost gateway, string slug) =>
        gateway.ChangeAsync(services => services.GetRequiredService<InboundService>().GetAsync(slug));
}
