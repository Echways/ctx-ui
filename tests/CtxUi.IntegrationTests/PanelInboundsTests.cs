using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Inbounds;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelInboundsTests
{
    private static Task<GatewayHost> StartWithoutPollingAsync() =>
        GatewayHost.StartAsync(builder => builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00");

    private static async Task<IPage> OpenInboundsAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Inbound'ы" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Inbound'ы" })).ToBeVisibleAsync();
        return session.Page;
    }

    private static ILocator Row(IPage page, string slug) =>
        page.GetByRole(AriaRole.Row).Filter(new() { HasText = $"/mcp/{slug}" });

    private static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog);

    private static Task<Inbound> StoredAsync(GatewayHost gateway, string slug) =>
        gateway.ChangeAsync(services => services.GetRequiredService<InboundService>().GetAsync(slug));

    [Fact]
    public async Task Empty_list_says_what_an_inbound_is()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenInboundsAsync(session);

        await Expect(page).ToHaveTitleAsync("Inbound'ы · ctx-ui");
        await Expect(page.GetByText("Inbound'ов пока нет")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Table)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Inbound_added_through_the_form_is_listed_and_saved_with_sealed_headers()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Dialog(page).GetByLabel("Slug").FillAsync("learn");
        await Dialog(page).GetByLabel("Название").FillAsync("Учебный");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync("https://learn.example.org/api/mcp");
        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("Authorization=Bearer s3cret\nX-Tenant=7");
        await Dialog(page).GetByLabel("Передавать upstream личность клиента").CheckAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(Row(page, "learn")).ToContainTextAsync("Учебный");
        await Expect(Row(page, "learn")).ToContainTextAsync("https://learn.example.org/api/mcp");
        await Expect(Row(page, "learn").GetByRole(AriaRole.Switch)).ToBeCheckedAsync();
        var stored = await StoredAsync(gateway, "learn");
        Assert.Equal("Учебный", stored.Name);
        Assert.True(stored.ForwardIdentity);
        Assert.Equal(Inbound.DefaultRequestTimeoutSeconds, stored.RequestTimeoutSeconds);
        Assert.Equal(Inbound.DefaultMaxBodyBytes, stored.MaxBodyBytes);
        Assert.DoesNotContain("s3cret", stored.UpstreamHeadersSealed);
        Assert.DoesNotContain("s3cret", await page.ContentAsync());
    }

    [Fact]
    public async Task Mistake_in_the_form_is_explained_in_place_and_can_be_corrected()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();

        await Dialog(page).GetByLabel("Slug").FillAsync("Мой сервер");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync("https://learn.example.org/mcp");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Slug «Мой сервер» недопустим");

        await Dialog(page).GetByLabel("Slug").FillAsync("echo");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Inbound «echo» уже есть");

        await Dialog(page).GetByLabel("Slug").FillAsync("learn");
        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("без знака равно");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("ключ=значение");

        await Dialog(page).GetByLabel("Заголовки для upstream").FillAsync("");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Expect(Row(page, "learn")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Row)).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task Cancelled_form_changes_nothing()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);
        await page.GetByRole(AriaRole.Button, new() { Name = "Добавить inbound" }).ClickAsync();
        await Dialog(page).GetByLabel("Slug").FillAsync("learn");
        await Dialog(page).GetByLabel("Адрес MCP-сервера").FillAsync("https://learn.example.org/mcp");

        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        await Expect(page.GetByText("Inbound'ов пока нет")).ToBeVisibleAsync();
    }

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

    [Fact]
    public async Task Switch_closes_and_reopens_the_inbound_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await Row(page, "echo").GetByRole(AriaRole.Switch).UncheckAsync();
        await Expect(page.GetByText("Inbound «echo» выключен")).ToBeVisibleAsync();
        using var closed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await Row(page, "echo").GetByRole(AriaRole.Switch).CheckAsync();
        await Expect(page.GetByText("Inbound «echo» включён")).ToBeVisibleAsync();
        using var open = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task Inbound_is_removed_only_after_the_admin_confirms()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.AddInboundAsync("docs", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo", "docs"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "echo"), "Удалить");
        await Expect(Dialog(page)).ToContainTextAsync("Клиентов с доступом к нему: 1");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
        await Expect(Row(page, "echo")).ToBeVisibleAsync();
        using var kept = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        await RowMenu.ChooseAsync(Row(page, "echo"), "Удалить");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Удалить" }).ClickAsync();
        await Expect(Row(page, "echo")).ToHaveCountAsync(0);
        using var gone = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var other = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/docs", issued.Token));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        await Expect(Row(page, "docs")).ToContainTextAsync("1");
    }

    [Fact]
    public async Task Inbound_removed_from_the_command_line_meanwhile_is_reported_not_crashed_on()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenInboundsAsync(session);
        await CliRunner.RunAsync(gateway.DataDir, "inbound", "remove", "echo");

        await Row(page, "echo").GetByRole(AriaRole.Switch).ClickAsync();

        await Expect(page.GetByText("Inbound «echo» не найден")).ToBeVisibleAsync();
        await Expect(Row(page, "echo")).ToHaveCountAsync(0);
    }
}
