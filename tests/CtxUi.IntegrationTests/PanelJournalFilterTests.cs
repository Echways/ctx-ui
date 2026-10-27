using Microsoft.Playwright;
using static CtxUi.IntegrationTests.JournalPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelJournalFilterTests
{
    [Fact]
    public async Task Journal_can_be_narrowed_to_one_client_and_widened_back_even_by_a_cut_off_link()
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
        await Expect(Rows(page)).ToHaveCountAsync(5);

        await ChooseClientAsync(page, "Петров");
        await Expect(Rows(page)).ToHaveCountAsync(4);
        await Expect(Rows(page).Filter(new() { HasText = "Иванов" })).ToHaveCountAsync(0);

        await ChooseClientAsync(page, "Все клиенты");
        await Expect(Rows(page)).ToHaveCountAsync(5);

        await page.GotoAsync("journal?client=1f0c2a");
        await Expect(Rows(page)).ToHaveCountAsync(5);
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

        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("удалён");
        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("echo");
    }
}
