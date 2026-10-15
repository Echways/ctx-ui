using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelConnectionTests
{
    private const string PublicUrl = "https://mcp.example.org";

    private static Task<GatewayHost> StartAsync() => GatewayHost.StartAsync(builder =>
    {
        builder.Configuration["CtxUi:RefreshInterval"] = "01:00:00";
        builder.Configuration["CtxUi:PublicUrl"] = PublicUrl + "/";
    });

    private static async Task<IPage> OpenClientsAsync(PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Клиенты" }).ClickAsync();
        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Клиенты" })).ToBeVisibleAsync();
        return session.Page;
    }

    private static ILocator Row(IPage page, string name) => page.GetByRole(AriaRole.Row).Filter(new() { HasText = name });

    private static ILocator Dialog(IPage page) => page.GetByRole(AriaRole.Dialog);

    private static ILocator Field(IPage page, string label) =>
        Dialog(page).GetByRole(AriaRole.Textbox, new() { Name = label, Exact = true });

    private static Task<string> ClipboardAsync(IPage page) => page.EvaluateAsync<string>("navigator.clipboard.readText()");

    [Fact]
    public async Task Dialog_shows_the_token_both_addresses_of_every_inbound_and_a_message_to_send()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("learn", "https://learn.example.org/mcp");
        await gateway.AddInboundAsync("docs", "https://docs.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов 4419", ["learn", "docs"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов 4419").GetByRole(AriaRole.Button, new() { Name = "Подключение" }).ClickAsync();

        await Expect(Dialog(page)).ToContainTextAsync("Подключение: Иванов 4419");
        await Expect(Field(page, "Токен")).ToHaveValueAsync(issued.Token);
        await Expect(Field(page, "Адрес learn")).ToHaveValueAsync($"{PublicUrl}/mcp/learn");
        await Expect(Field(page, "Адрес learn с токеном")).ToHaveValueAsync($"{PublicUrl}/k/{issued.Token}/mcp/learn");
        await Expect(Field(page, "Адрес docs")).ToHaveValueAsync($"{PublicUrl}/mcp/docs");
        await Expect(Field(page, "Сообщение для клиента")).ToHaveValueAsync(
            ConnectionTextOf(issued.Client.Name, issued.Token));

        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Закрыть" }).ClickAsync();
        await Expect(Dialog(page)).ToHaveCountAsync(0);
    }

    private static string ConnectionTextOf(string name, string token) =>
        $"""
        Доступ к MCP-серверам для «{name}».

        Токен: {token}

        docs
          адрес: {PublicUrl}/mcp/docs
          заголовок: Authorization: Bearer {token}
          если заголовок задать нельзя, адрес с токеном внутри:
          {PublicUrl}/k/{token}/mcp/docs

        learn
          адрес: {PublicUrl}/mcp/learn
          заголовок: Authorization: Bearer {token}
          если заголовок задать нельзя, адрес с токеном внутри:
          {PublicUrl}/k/{token}/mcp/learn

        Доступ бессрочный.
        Токен и адрес с токеном равносильны паролю: не передавайте их дальше.
        """.ReplaceLineEndings("\n");

    [Fact]
    public async Task Copy_buttons_put_the_token_an_address_and_the_message_on_the_clipboard()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("learn", "https://learn.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["learn"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);
        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Подключение" }).ClickAsync();

        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Скопировать токен" }).ClickAsync();
        await Expect(page.GetByText("Скопировано")).ToBeVisibleAsync();
        var token = await ClipboardAsync(page);
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Скопировать адрес learn с токеном" }).ClickAsync();
        await Eventually.TrueAsync(async () => await ClipboardAsync(page) != token, "адрес попал в буфер обмена");
        var address = await ClipboardAsync(page);
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Скопировать сообщение" }).ClickAsync();
        await Eventually.TrueAsync(async () => await ClipboardAsync(page) != address, "сообщение попало в буфер обмена");
        var message = await ClipboardAsync(page);

        Assert.Equal(issued.Token, token);
        Assert.Equal($"{PublicUrl}/k/{issued.Token}/mcp/learn", address);
        Assert.StartsWith("Доступ к MCP-серверам для «Иванов».", message);
    }

    [Fact]
    public async Task Address_with_the_token_taken_from_the_dialog_is_accepted_by_the_gateway()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("learn", upstream.McpUrl);
        await gateway.AddClientAsync("Иванов", ["learn"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);
        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Подключение" }).ClickAsync();

        var shown = await Field(page, "Адрес learn с токеном").InputValueAsync();
        using var response = await gateway.Http.SendAsync(GatewayHost.Post(shown[PublicUrl.Length..]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task New_token_is_issued_only_after_confirmation_and_the_old_one_stops_at_once()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("learn", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["learn"]);
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Новый токен" }).ClickAsync();
        await Expect(Dialog(page)).ToContainTextAsync("Прежний токен сразу перестанет работать");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отмена" }).ClickAsync();
        using var kept = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/learn", issued.Token));

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Новый токен" }).ClickAsync();
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Выпустить" }).ClickAsync();
        await Expect(Dialog(page)).ToContainTextAsync("Подключение: Иванов");
        var fresh = await Field(page, "Токен").InputValueAsync();
        using var old = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/learn", issued.Token));
        using var renewed = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/learn", fresh));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.True(AccessToken.IsWellFormed(fresh));
        Assert.NotEqual(issued.Token, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    [Fact]
    public async Task Token_whose_saved_copy_cannot_be_opened_is_explained_instead_of_shown()
    {
        await using var gateway = await StartAsync();
        await gateway.AddInboundAsync("learn", "https://learn.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["learn"]);
        Directory.Delete(Path.Combine(gateway.DataDir, "keys"), recursive: true);
        await gateway.RestartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Button, new() { Name = "Подключение" }).ClickAsync();

        await Expect(Dialog(page).GetByRole(AriaRole.Alert)).ToContainTextAsync("Токен показать нельзя");
        await Expect(Field(page, "Токен")).ToHaveCountAsync(0);
        Assert.DoesNotContain(issued.Token, await page.ContentAsync());
    }
}
