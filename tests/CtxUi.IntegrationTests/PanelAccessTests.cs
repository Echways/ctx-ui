using System.Net;
using Microsoft.Extensions.Logging;

namespace CtxUi.IntegrationTests;

public class PanelAccessTests
{
    private const string Refusal = "Неверный логин или пароль";

    private static string Cookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.SingleOrDefault(value => value.StartsWith("ctx-ui=", StringComparison.Ordinal)) ?? ""
            : "";

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/logout")]
    [InlineData("/inbounds")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_content/CtxUi.Panel/panel.css")]
    [InlineData("/abcdefgh12345678/login")]
    public async Task Without_the_secret_path_the_panel_does_not_exist(string path)
    {
        await using var gateway = await GatewayHost.StartAsync();

        using var response = await gateway.Http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/")]
    [InlineData("/mcp/echo/extra")]
    [InlineData("/mcp/echo/login")]
    public async Task Addresses_under_the_gateway_path_never_reach_the_panel(string path)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(gateway.BaseUrl) };

        using var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Secret_path_typed_without_the_slash_leads_to_the_one_with_it()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var response = await gateway.Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, panel.Root + "?from=bookmark"), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(panel.Root + "/login", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Visitor_who_has_not_signed_in_is_sent_to_the_login_form()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var home = await panel.GetAsync("");
        using var hub = await panel.GetAsync("_blazor/negotiate");

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal($"{panel.Root}/login", home.Headers.Location!.AbsolutePath);
        Assert.Equal($"?ReturnUrl={Uri.EscapeDataString(panel.Root + "/")}", home.Headers.Location.Query);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
    }

    [Fact]
    public async Task Login_form_comes_with_its_style_and_knows_where_the_panel_lives()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var form = await panel.GetAsync("login");
        using var style = await panel.GetAsync("_content/CtxUi.Panel/panel.css");
        using var script = await panel.GetAsync("_framework/blazor.web.js");
        var html = await PanelClient.PageAsync(form);

        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains($"<base href=\"{panel.Root}/\"", html);
        Assert.Contains("name=\"Input.Password\"", html);
        Assert.Contains("<meta name=\"referrer\" content=\"same-origin\"", html);
        Assert.DoesNotContain(Refusal, html);
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
    }

    [Fact]
    public async Task Panel_brings_its_own_fonts_icon_and_night_colour()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var form = await panel.GetAsync("login");
        using var sans = await panel.GetAsync("_content/CtxUi.Panel/fonts/onest.woff2");
        using var mono = await panel.GetAsync("_content/CtxUi.Panel/fonts/martian-mono.woff2");
        using var icon = await panel.GetAsync("_content/CtxUi.Panel/favicon.svg");
        var html = await PanelClient.PageAsync(form);

        Assert.Contains("<meta name=\"theme-color\" content=\"#080C1C\"", html);
        Assert.Contains("rel=\"icon\"", html);
        Assert.Contains("rel=\"preload\"", html);
        Assert.Equal(HttpStatusCode.OK, sans.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mono.StatusCode);
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Right_password_opens_the_panel()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);

        using var signedIn = await panel.SignInAsync("Root", admin.Password);
        using var home = await panel.GetAsync("");

        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(gateway.PanelUrl, signedIn.Headers.Location!.OriginalString);
        var cookie = Cookie(signedIn);
        Assert.Contains($"path={panel.Root}", cookie);
        Assert.Contains("httponly", cookie);
        Assert.DoesNotContain("secure", cookie);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("_framework/blazor.web.js", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_login_get_the_same_answer_and_no_cookie()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);

        using var wrongPassword = await panel.SignInAsync("root", "not-the-password-1234");
        using var unknownLogin = await panel.SignInAsync("nobody", "not-the-password-1234");
        using var home = await panel.GetAsync("");

        Assert.Equal(HttpStatusCode.OK, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknownLogin.StatusCode);
        Assert.Contains(Refusal, await PanelClient.PageAsync(wrongPassword));
        Assert.Contains(Refusal, await PanelClient.PageAsync(unknownLogin));
        Assert.Equal("", Cookie(wrongPassword));
        Assert.Equal("", Cookie(unknownLogin));
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_close_the_door_until_a_new_password_is_issued()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await panel.SignInAsync("root", "not-the-password-1234");
        }

        using var locked = await panel.SignInAsync("root", admin.Password);
        var listed = await CliRunner.RunAsync(gateway.DataDir, "admin", "list");
        var reissued = await CliRunner.RunAsync(gateway.DataDir, "admin", "password", "root");
        var fresh = reissued.Output.Split('\n').Single(line => line.StartsWith("Пароль:", StringComparison.Ordinal))[9..].Trim();
        using var reopened = await panel.SignInAsync("root", fresh);

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains(Refusal, await PanelClient.PageAsync(locked));
        Assert.Contains("вход закрыт до", listed.Output);
        Assert.Equal(HttpStatusCode.Redirect, reopened.StatusCode);
    }

    [Fact]
    public async Task Form_that_did_not_come_from_the_panel_is_refused()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);

        using var forged = await panel.PostAsync(
            "login",
            new Dictionary<string, string> { ["_handler"] = "login", ["Input.Name"] = "root", ["Input.Password"] = admin.Password },
            withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("", Cookie(forged));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/elsewhere")]
    public async Task After_login_the_visitor_is_never_sent_outside_the_panel(string returnUrl)
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);

        using var signedIn = await panel.SignInAsync("root", admin.Password, "?ReturnUrl=" + Uri.EscapeDataString(returnUrl));

        Assert.Equal(gateway.PanelUrl, signedIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task After_login_the_visitor_returns_to_the_page_they_asked_for()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);
        using var asked = await panel.GetAsync("logout?from=menu");

        using var signedIn = await panel.SignInAsync("root", admin.Password, asked.Headers.Location!.Query);

        Assert.Equal(gateway.PanelUrl + "logout?from=menu", signedIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);
        using var signedIn = await panel.SignInAsync("root", admin.Password);

        using var signedOut = await panel.SignOutAsync();
        using var home = await panel.GetAsync("");

        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal(gateway.PanelUrl + "login", signedOut.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
    }

    [Fact]
    public async Task Behind_a_tls_proxy_the_cookie_is_secure_and_redirects_keep_https()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway, forwardedProto: "https");

        using var home = await panel.GetAsync("");
        using var signedIn = await panel.SignInAsync("root", admin.Password);

        Assert.Equal("https", home.Headers.Location!.Scheme);
        Assert.Contains("secure", Cookie(signedIn));
    }

    [Fact]
    public async Task Removed_admin_cannot_sign_in()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var admin = await gateway.AddAdminAsync();
        await CliRunner.RunAsync(gateway.DataDir, "admin", "remove", "root");
        using var panel = new PanelClient(gateway);

        using var refused = await panel.SignInAsync("root", admin.Password);

        Assert.Contains(Refusal, await PanelClient.PageAsync(refused));
    }

    [Fact]
    public async Task Gateway_keeps_answering_beside_the_panel()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var byHeader = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        using var byPath = await gateway.Http.SendAsync(GatewayHost.Post($"/k/{issued.Token}/mcp/echo"));
        using var noToken = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo"));

        Assert.Equal(HttpStatusCode.OK, byHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byPath.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

    [Fact]
    public async Task Neither_the_secret_path_nor_the_password_reach_the_server_log()
    {
        var logs = new CapturingLogs();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);

        using var wrong = await panel.SignInAsync("root", "not-the-password-1234");
        using var signedIn = await panel.SignInAsync("root", admin.Password);
        using var home = await panel.GetAsync("");
        using var missing = await panel.GetAsync("no-such-page");

        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(gateway.PanelPath));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(admin.Password));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("not-the-password-1234"));
    }

    [Fact]
    public async Task Server_says_at_start_how_to_let_the_first_admin_in()
    {
        var logs = new CapturingLogs();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        Assert.Contains(logs.Lines, line => line.Contains("ctx-ui admin add"));

        await gateway.AddAdminAsync();
        logs.Lines.Clear();
        await gateway.RestartAsync();

        Assert.DoesNotContain(logs.Lines, line => line.Contains("ctx-ui admin add"));
    }
}
