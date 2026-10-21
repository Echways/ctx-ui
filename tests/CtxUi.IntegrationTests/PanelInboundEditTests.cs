using System.Net;
using Microsoft.Playwright;
using static CtxUi.IntegrationTests.InboundPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelInboundEditTests
{
    [Fact]
    public async Task Edited_address_and_limits_take_effect_on_the_next_request()
    {
        await using var first = await FakeUpstream.StartAsync();
        await using var second = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", first.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await Row(page, "echo").GetByRole(AriaRole.Button, new() { Name = "Изменить" }).ClickAsync();
        await Expect(Dialog(page).GetByLabel("Slug")).ToBeDisabledAsync();
        await Expect(Dialog(page).GetByLabel("Адрес MCP-сервера")).ToHaveValueAsync(first.McpUrl);
        await Dialog(page).GetByLabel("Название").FillAsync("Эхо");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync(second.McpUrl);
        await Dialog(page).GetByLabel("Таймаут запроса, с").FillAsync("30");
        await Dialog(page).GetByLabel("Предел тела запроса, КБ").FillAsync("1");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Expect(Row(page, "echo")).ToContainTextAsync("Эхо");

        using var served = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var tooLarge = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, new string(' ', 2_000) + "{}"));

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Empty(first.Requests);
        Assert.Single(second.Requests);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        var stored = await StoredAsync(gateway, "echo");
        Assert.Equal(30, stored.RequestTimeoutSeconds);
        Assert.Equal(1_024, stored.MaxBodyBytes);
    }

    [Fact]
    public async Task Stored_headers_show_only_their_names_and_stay_unless_replaced()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl, [KeyValuePair.Create("X-Api-Key", "s3cret")]);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await Row(page, "echo").GetByRole(AriaRole.Button, new() { Name = "Изменить" }).ClickAsync();
        await Expect(Dialog(page)).ToContainTextAsync("Заданы заголовки: X-Api-Key");
        Assert.DoesNotContain("s3cret", await page.ContentAsync());
        await Dialog(page).GetByLabel("Название").FillAsync("Эхо");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Expect(Row(page, "echo")).ToContainTextAsync("Эхо");
        using var kept = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await Row(page, "echo").GetByRole(AriaRole.Button, new() { Name = "Изменить" }).ClickAsync();
        await Dialog(page).GetByLabel("Заменить заголовки").CheckAsync();
        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("Authorization=Bearer new");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        using var replaced = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await Row(page, "echo").GetByRole(AriaRole.Button, new() { Name = "Изменить" }).ClickAsync();
        await Expect(Dialog(page)).ToContainTextAsync("Заданы заголовки: Authorization");
        await Dialog(page).GetByLabel("Заменить заголовки").CheckAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Сохранить" }).ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
        using var removed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        var requests = upstream.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("s3cret", requests[0].Headers["X-Api-Key"]);
        Assert.Equal("Bearer new", requests[1].Headers["Authorization"]);
        Assert.False(requests[1].Headers.ContainsKey("X-Api-Key"));
        Assert.False(requests[2].Headers.ContainsKey("Authorization"));
    }
}
