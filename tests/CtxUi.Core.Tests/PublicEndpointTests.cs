using CtxUi.Core.Settings;

namespace CtxUi.Core.Tests;

public class PublicEndpointTests
{
    [Theory]
    [InlineData("https://EXAMPLE.org:443///", "https://example.org")]
    [InlineData("https://example.org:9443/", "https://example.org:9443")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    [InlineData("https://bücher.example/", "https://xn--bcher-kva.example")]
    [InlineData("https://[2001:db8::1]:9443/", "https://[2001:db8::1]:9443")]
    public void Public_urls_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, PublicAddress.Normalize(input));
    }

    [Theory]
    [InlineData("http://example.org")]
    [InlineData("ftp://example.org")]
    [InlineData("https://user@example.org")]
    [InlineData("https://example.org/base")]
    [InlineData("https://example.org/.")]
    [InlineData("https://example.org?")]
    [InlineData("https://example.org#")]
    [InlineData("https://example.org:0")]
    [InlineData("https://example.org:")]
    [InlineData("https://2130706433")]
    [InlineData("https://invalid_host.example")]
    [InlineData("https://example.org.")]
    [InlineData("https://example.org\\")]
    public void Invalid_public_urls_are_rejected(string input)
    {
        Assert.Null(PublicAddress.Normalize(input));
    }

    [Fact]
    public void Profiles_with_the_same_origin_keep_the_existing_audience()
    {
        const string legacy = "https://EXAMPLE.org:443";
        var endpoints = new PublicEndpoints(legacy,
            [new HarnessProfile("alias", "Alias", true, "https://example.org")], false);

        Assert.Equal(legacy, endpoints.Select(null));
        Assert.Equal(legacy, endpoints.Select("alias"));
        Assert.Equal(legacy, endpoints.ForRequest("https", "example.org"));
        Assert.Equal(legacy + "/mcp/uni", endpoints.Match("https://example.org/mcp/uni")?.Url);
        Assert.Null(endpoints.ForRequest("https", "example.org:9443"));
    }
}
