using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class RedirectUrisTests
{
    [Theory]
    [InlineData("http://localhost/callback", "http://localhost:3118/callback", true)]
    [InlineData("http://localhost:1/callback", "http://localhost:3118/callback", true)]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:55001/callback", true)]
    [InlineData("http://[::1]/callback", "http://[::1]:55001/callback", true)]
    [InlineData("http://localhost/callback", "http://localhost:3118/other", false)]
    [InlineData("http://localhost/callback", "http://127.0.0.1:3118/callback", false)]
    [InlineData("http://localhost/callback", "http://localhost:3118/callback?x=1", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai:8443/api/mcp/auth_callback", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://Claude.ai/api/mcp/auth_callback", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback/", false)]
    [InlineData("https://example.org/callback", "https://example.org:443/callback", false)]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", "cursor://anysphere.cursor-mcp/oauth/callback", true)]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", "cursor://anysphere.cursor-mcp/oauth/other", false)]
    public void Redirect_uri_matching(string registered, string requested, bool matches) =>
        Assert.Equal(matches, RedirectUris.Matches(registered, requested));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:3118/callback", true)]
    [InlineData("http://127.0.0.1/callback", true)]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", true)]
    [InlineData("com.example.app:/oauth", true)]
    [InlineData("http://example.org/callback", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://example.org/callback#fragment", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    public void Registration_accepts_https_loopback_and_app_schemes(string uri, bool allowed) =>
        Assert.Equal(allowed, RedirectUris.IsAllowedForRegistration(uri));

    [Theory]
    [InlineData("http://localhost:3118/callback", true)]
    [InlineData("http://127.0.0.1/callback", true)]
    [InlineData("http://[::1]:1/callback", true)]
    [InlineData("https://localhost/callback", false)]
    [InlineData("https://claude.ai/callback", false)]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", false)]
    public void Loopback_means_plain_http_to_this_machine(string uri, bool loopback) =>
        Assert.Equal(loopback, RedirectUris.IsLoopback(uri));
}
