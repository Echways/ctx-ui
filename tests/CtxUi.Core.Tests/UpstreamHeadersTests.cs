using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Tests;

public class UpstreamHeadersTests
{
    private static KeyValuePair<string, string>[] Pair(string name, string value) => [KeyValuePair.Create(name, value)];

    [Fact]
    public void Valid_headers_are_copied()
    {
        var headers = UpstreamHeaders.Parse(Pair("Authorization", "Bearer upstream-key"));

        Assert.Equal("Bearer upstream-key", headers["authorization"]);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-length")]
    [InlineData("Content-Type")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("X-Mcp-Client-Id")]
    [InlineData("x-mcp-meta-admin")]
    [InlineData("Bad Header")]
    [InlineData("")]
    public void Forbidden_or_malformed_name_is_rejected(string name) =>
        Assert.Throws<DomainException>(() => UpstreamHeaders.Parse(Pair(name, "x")));

    [Theory]
    [InlineData("a\r\nX-Evil: 1")]
    [InlineData("ключ")]
    [InlineData("")]
    public void Value_must_be_printable_ascii(string value) =>
        Assert.Throws<DomainException>(() => UpstreamHeaders.Parse(Pair("X-Api-Key", value)));

    [Fact]
    public void Repeated_name_is_rejected() =>
        Assert.Throws<DomainException>(() => UpstreamHeaders.Parse(
            [KeyValuePair.Create("X-Api-Key", "1"), KeyValuePair.Create("x-api-key", "2")]));

    [Fact]
    public void Sealed_headers_open_back()
    {
        var vault = new FakeVault();
        var sealedHeaders = UpstreamHeaders.Seal(vault, UpstreamHeaders.Parse(Pair("X-Api-Key", "s3cret")));

        Assert.DoesNotContain("s3cret", sealedHeaders);
        Assert.Equal("s3cret", UpstreamHeaders.Open(vault, sealedHeaders)!["X-Api-Key"]);
    }

    [Fact]
    public void No_headers_are_stored_as_null_and_open_as_empty()
    {
        var vault = new FakeVault();

        Assert.Null(UpstreamHeaders.Seal(vault, new Dictionary<string, string>()));
        Assert.Empty(UpstreamHeaders.Open(vault, null)!);
    }

    [Fact]
    public void Unreadable_headers_open_as_null()
    {
        var vault = new FakeVault();
        var sealedHeaders = UpstreamHeaders.Seal(vault, UpstreamHeaders.Parse(Pair("X-Api-Key", "s3cret")));
        vault.Broken = true;

        Assert.Null(UpstreamHeaders.Open(vault, sealedHeaders));
    }
}
