using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

internal static class ClientPages
{
    public const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    public static Task<GatewayHost> StartWithoutPollingAsync() =>
        GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");

    public static async Task<IPage> OpenClientsAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Клиенты" })).ToBeVisibleAsync();
        return session.Page;
    }

    public static ILocator Row(IPage page, string name) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = name });

    public static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog);

    public static ILocator Field(IPage page, string label) =>
        Dialog(page).GetByRole(AriaRole.Textbox, new() { Name = label, Exact = true });

    public static Task SaveAsync(IPage page) => Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();

    public static Task AddAsync(IPage page) => Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить клиента" }).ClickAsync();

    public static Task<Client> StoredAsync(GatewayHost gateway, string name) =>
        gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync(name));

    public static async Task<bool> IsQuotaRefusalAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Contains("Лимит вызовов исчерпан");
}
