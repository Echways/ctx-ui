using System.Net;
using static CtxUi.IntegrationTests.PanelResponses;

namespace CtxUi.IntegrationTests;

public class PanelLoginTests
{
    [Fact]
    public async Task Login_form_comes_with_its_style_and_knows_where_the_panel_lives()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var panel = new PanelClient(gateway);

        using var form = await panel.GetAsync("login");
        using var style = await panel.GetAsync("_content/Enfilade/enfilade.css");
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
        using var sans = await panel.GetAsync("_content/Enfilade/fonts/onest.woff2");
        using var mono = await panel.GetAsync("_content/Enfilade/fonts/martian-mono.woff2");
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
}
