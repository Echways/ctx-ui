using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static CtxUi.IntegrationTests.JournalPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelJournalTests
{
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

        await Expect(Rows(page)).ToHaveCountAsync(6);
        await Expect(Rows(page).Nth(1)).ToHaveTextAsync(DateTimeOffset.UtcNow.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru")));
        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(1)).ToHaveTextAsync("Иванов");
        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("echo");
        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/call echo");
        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("квота исчерпана");
        await Expect(Rows(page).Nth(3).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("выполнен");
        await Expect(Rows(page).Nth(4).GetByRole(AriaRole.Cell).Nth(2)).ToHaveTextAsync("broken");
        await Expect(Rows(page).Nth(4).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/call search");
        await Expect(Rows(page).Nth(4).GetByRole(AriaRole.Cell).Nth(4)).ToHaveTextAsync("сбой upstream HTTP 500");
        await Expect(Rows(page).Nth(5).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/list");
        await Expect(Rows(page).Nth(5).GetByRole(AriaRole.Cell).First).ToHaveTextAsync(
            new System.Text.RegularExpressions.Regex(@"^\d{2}:\d{2}:\d{2}$"));
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

        await Expect(Rows(page).Nth(2).GetByRole(AriaRole.Cell).Nth(3)).ToHaveTextAsync("tools/call " + hostile);
        await Expect(page.Locator("td img")).ToHaveCountAsync(0);
        await Expect(page).ToHaveTitleAsync("Журнал · ctx-ui");
    }

    [Fact]
    public async Task Journal_shows_the_device()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        flow.Browser.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        var tokens = await flow.ExchangeAsync();
        using var viaDevice = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", tokens.Access, ToolCall("echo")));
        using var viaKey = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall("echo")));
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenJournalAsync(session);

        await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Chrome · Windows" })).ToHaveCountAsync(1);
        await Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Иванов" })).ToHaveCountAsync(2);
    }
}
