using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.Usage;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelJournalTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private static string ToolCall(string tool) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}";

    private static async Task<IPage> OpenJournalAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Журнал" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Журнал" })).ToBeVisibleAsync();
        return session.Page;
    }

    private static ILocator Rows(IPage page) => page.GetByRole(AriaRole.Row);

    private static async Task ChooseClientAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Клиент" }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = name, Exact = true }).ClickAsync();
    }

    private static Task JournalHasAsync(GatewayHost gateway, int calls) => Eventually.TrueAsync(
        () => gateway.ChangeAsync(async services =>
            (await services.GetRequiredService<UsageStore>().RecentAsync(null, 100)).Count == calls),
        "журнал дошёл до базы");

    private static async Task CallAsync(GatewayHost gateway, string inbound, string token, string body)
    {
        using var response = await gateway.Http.SendAsync(GatewayHost.Post($"/mcp/{inbound}", token, body));
    }

    [Fact]
    public async Task Empty_journal_says_so()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenJournalAsync(session);

        await Expect(page).ToHaveTitleAsync("Журнал");
        await Expect(page.GetByText("Журнал пуст")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Calls_are_listed_newest_first_with_who_what_and_how_it_ended()
    {
        await using var upstream = await FakeUpstream.StartAsync((app, _) =>
            app.Map("/broken", context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("broken", upstream.BaseUrl + "/broken");
        var issued = await gateway.AddClientAsync("Иванов", ["echo", "broken"], limits: new ClientLimits(QuotaCalls: 1));
        await CallAsync(gateway, "echo", issued.Token, ToolsList);
        await CallAsync(gateway, "broken", issued.Token, ToolCall("search"));
        await CallAsync(gateway, "echo", issued.Token, ToolCall("echo"));
        await CallAsync(gateway, "echo", issued.Token, ToolCall("echo"));
        await JournalHasAsync(gateway, 4);
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenJournalAsync(session);

        await Expect(Rows(page)).ToHaveCountAsync(5);
        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("Иванов");
        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("echo");
        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/call echo");
        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("квота исчерпана");
        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("выполнен");
        await Expect(Rows(page).Nth(3).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("broken");
        await Expect(Rows(page).Nth(3).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/call search");
        await Expect(Rows(page).Nth(3).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("сбой upstream");
        await Expect(Rows(page).Nth(4).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/list");
        await Expect(Rows(page).Nth(4).GetByRole(AriaRole.Cell).First).ToHaveTextAsync(
            new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$"));
    }

    [Fact]
    public async Task Journal_can_be_narrowed_to_one_client_and_widened_back()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var ivanov = await gateway.AddClientAsync("Иванов", ["echo"]);
        var petrov = await gateway.AddClientAsync("Петров", ["echo"]);
        await CallAsync(gateway, "echo", ivanov.Token, ToolCall("echo"));
        await CallAsync(gateway, "echo", petrov.Token, ToolCall("echo"));
        await CallAsync(gateway, "echo", petrov.Token, ToolsList);
        await JournalHasAsync(gateway, 3);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenJournalAsync(session);
        await Expect(Rows(page)).ToHaveCountAsync(4);

        await ChooseClientAsync(page, "Петров");
        await Expect(Rows(page)).ToHaveCountAsync(3);
        await Expect(Rows(page).Filter(new() { HasText = "Иванов" })).ToHaveCountAsync(0);

        await ChooseClientAsync(page, "Все клиенты");
        await Expect(Rows(page)).ToHaveCountAsync(4);
    }

    [Fact]
    public async Task Calls_of_a_removed_client_stay_and_are_marked_so()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await CallAsync(gateway, "echo", issued.Token, ToolCall("echo"));
        await JournalHasAsync(gateway, 1);
        await CliRunner.RunAsync(gateway.DataDir, "client", "remove", "Иванов");
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenJournalAsync(session);

        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("удалён");
        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("echo");
    }

    [Fact]
    public async Task Tool_name_sent_by_a_client_is_shown_as_text_and_never_run_as_markup()
    {
        const string hostile = "<img src=x onerror=document.title='hacked'>";
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await CallAsync(gateway, "echo", issued.Token, ToolCall(hostile));
        await JournalHasAsync(gateway, 1);
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenJournalAsync(session);

        await Expect(Rows(page).Nth(1).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/call " + hostile);
        await Expect(page.Locator("td img")).ToHaveCountAsync(0);
        await Expect(page).ToHaveTitleAsync("Журнал");
    }

    [Fact]
    public async Task Refresh_button_brings_the_calls_made_since_the_page_was_opened()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenJournalAsync(session);
        await Expect(page.GetByText("Журнал пуст")).ToBeVisibleAsync();
        await CallAsync(gateway, "echo", issued.Token, ToolsList);
        await JournalHasAsync(gateway, 1);

        await page.GetByRole(AriaRole.Button, new() { Name = "Обновить" }).ClickAsync();

        await Expect(Rows(page)).ToHaveCountAsync(2);
    }
}
