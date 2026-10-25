using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class AppIdentityTests
{
    [Fact]
    public void A_metadata_document_names_its_host_as_verifier()
    {
        var app = AppIdentity.Of("https://claude.ai/oauth/mcp-oauth-client-metadata", "Claude", ["https://claude.ai/api/mcp/auth_callback"]);

        Assert.Equal("claude.ai", app.VerifiedBy);
    }

    [Fact]
    public void A_registered_app_is_not_verified()
    {
        var app = AppIdentity.Of(null, "Claude", ["https://evil.example/callback"]);

        Assert.Null(app.VerifiedBy);
        Assert.Equal(["evil.example"], app.RedirectHosts);
    }

    [Fact]
    public void An_international_host_is_shown_in_ascii()
    {
        var app = AppIdentity.Of("https://клод.рф/metadata", "Claude", ["https://клод.рф/callback"]);

        Assert.Equal("xn--d1aldm.xn--p1ai", app.VerifiedBy);
    }
}
