using Microsoft.Playwright;
using static CtxUi.IntegrationTests.JournalPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelJournalRefreshTests
{
    [Fact]
    public async Task Open_journal_rereads_itself()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(
            builder => builder.Configuration["CtxUi:AutoRefreshInterval"] = "00:00:00.300");
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenJournalAsync(session);
        await Expect(page.GetByText("Журнал пуст")).ToBeVisibleAsync();

        await CallAsync(gateway, "echo", issued.Token, ToolsList);

        await Expect(Rows(page)).ToHaveCountAsync(3);
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

        await Expect(Rows(page)).ToHaveCountAsync(3);
    }
}
