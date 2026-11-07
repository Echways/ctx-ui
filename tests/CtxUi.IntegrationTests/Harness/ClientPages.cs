using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

internal static class ClientPages
{
    public const string ToolCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    public static Task<GatewayHost> StartWithoutPollingAsync()
    {
        return GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");
    }

    public static async Task<IPage> OpenClientsAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation)
            .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Клиенты" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Клиенты" }))
            .ToBeVisibleAsync();
        return session.Page;
    }

    public static ILocator Row(IPage page, string name)
    {
        return page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = name });
    }

    public static ILocator Dialog(IPage page)
    {
        return page.GetByRole(AriaRole.Dialog);
    }

    public static ILocator Field(IPage page, string label)
    {
        return Dialog(page).GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = label, Exact = true });
    }

    public static Task SaveAsync(IPage page)
    {
        return Dialog(page).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Сохранить" }).ClickAsync();
    }

    public static Task AddAsync(IPage page)
    {
        return Dialog(page).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Добавить клиента" })
            .ClickAsync();
    }

    public static Task<Client> StoredAsync(GatewayHost gateway, string name)
    {
        return gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync(name));
    }

    public static async Task<bool> IsQuotaRefusalAsync(HttpResponseMessage response)
    {
        return (await response.Content.ReadAsStringAsync()).Contains("Лимит вызовов исчерпан");
    }
}
