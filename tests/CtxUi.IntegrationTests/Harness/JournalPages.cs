using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Usage;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

internal static class JournalPages
{
    public const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    public static string ToolCall(string tool) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}";

    public static async Task<IPage> OpenJournalAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Журнал" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Журнал" })).ToBeVisibleAsync();
        return session.Page;
    }

    public static ILocator Rows(IPage page) => page.GetByRole(AriaRole.Row);

    public static async Task ChooseClientAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Клиент" }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true }).ClickAsync();
    }

    public static Task JournalHasAsync(GatewayHost gateway, int calls) => Eventually.TrueAsync(
        () => gateway.ChangeAsync(async services =>
            (await services.GetRequiredService<UsageStore>().RecentAsync(null, 100)).Count == calls),
        "журнал дошёл до базы");

    public static async Task CallAsync(GatewayHost gateway, string inbound, string token, string body)
    {
        using var response = await gateway.Http.SendAsync(GatewayHost.Post($"/mcp/{inbound}", token, body));
    }
}
