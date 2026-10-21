using System.Net;
using static CtxUi.IntegrationTests.PanelResponses;

namespace CtxUi.IntegrationTests;

public class PanelRedirectTests
{
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
}
