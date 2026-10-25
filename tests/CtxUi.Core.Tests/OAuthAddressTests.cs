using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class OAuthAddressTests
{
    [Theory]
    [InlineData("https://mcp.example.org", true)]
    [InlineData("https://mcp.example.org/", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:5000/", true)]
    [InlineData("http://[::1]:5000", true)]
    [InlineData("http://mcp.example.org", false)]
    [InlineData("http://127.0.0.2", false)]
    [InlineData("mcp.example.org", false)]
    [InlineData("", false)]
    public void Oauth_needs_https_or_a_loopback_address(string publicUrl, bool allowed) =>
        Assert.Equal(allowed, OAuthAvailability.IsAllowed(publicUrl));

    [Fact]
    public void Resource_is_the_inbound_address_without_trailing_slash() =>
        Assert.Equal("https://mcp.example.org/mcp/learn", OAuthResource.For("https://mcp.example.org/", "learn"));

    [Fact]
    public void Issuer_is_the_public_url_without_trailing_slash() =>
        Assert.Equal("https://mcp.example.org", OAuthResource.Issuer("https://mcp.example.org/"));

    [Fact]
    public void Metadata_url_is_the_well_known_path_of_the_inbound() =>
        Assert.Equal(
            "https://mcp.example.org/.well-known/oauth-protected-resource/mcp/learn",
            OAuthResource.MetadataUrl("https://mcp.example.org/", "learn"));

    [Theory]
    [InlineData("https://mcp.example.org/mcp/learn", true)]
    [InlineData("https://MCP.example.org/mcp/learn", true)]
    [InlineData("HTTPS://mcp.example.org/mcp/learn/", true)]
    [InlineData("https://mcp.example.org/mcp/Learn", false)]
    [InlineData("https://mcp.example.org/mcp/learn//", false)]
    [InlineData("https://mcp.example.org/mcp/learn?x=1", false)]
    [InlineData("https://mcp.example.org/mcp/learn/more", false)]
    [InlineData("https://mcp.example.org:8443/mcp/learn", false)]
    [InlineData("http://mcp.example.org/mcp/learn", false)]
    [InlineData("https://other.example.org/mcp/learn", false)]
    [InlineData("learn", false)]
    public void Requested_resource_maps_to_a_slug(string requested, bool found)
    {
        Assert.Equal(found, OAuthResource.TrySlug("https://mcp.example.org/", requested, out var slug));
        if (found)
            Assert.Equal("learn", slug);
    }
}
