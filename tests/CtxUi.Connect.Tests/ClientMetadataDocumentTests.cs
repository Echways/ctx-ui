using System.Net.Http.Headers;
using System.Text;
using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class ClientMetadataDocumentTests
{
    private static readonly Uri Url = new("https://claude.ai/oauth/mcp-oauth-client-metadata");

    private static ClientMetadataDocument? Parse(string json, CacheControlHeaderValue? cache = null, Uri? url = null) =>
        ClientMetadataDocument.Parse(url ?? Url, Encoding.UTF8.GetBytes(json), cache);

    [Fact]
    public void Valid_document_is_read()
    {
        var document = Parse("""
            {"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude",
             "redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"token_endpoint_auth_method":"none",
             "grant_types":["authorization_code","refresh_token"]}
            """);

        Assert.NotNull(document);
        Assert.Equal("https://claude.ai/oauth/mcp-oauth-client-metadata", document.ClientId);
        Assert.Equal("Claude", document.ClientName);
        Assert.Equal(["https://claude.ai/api/mcp/auth_callback"], document.RedirectUris);
        Assert.Equal("none", document.TokenEndpointAuthMethod);
    }

    [Theory]
    [InlineData("https://claude.ai/oauth/mcp-oauth-client-metadata/")]
    [InlineData("https://CLAUDE.ai/oauth/mcp-oauth-client-metadata")]
    [InlineData("https://claude.ai/oauth/other")]
    public void Client_id_must_match_the_url_exactly(string clientId) =>
        Assert.Null(Parse($$"""{"client_id":"{{clientId}}","client_name":"Claude","redirect_uris":["https://claude.ai/cb"]}"""));

    [Theory]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","redirect_uris":["https://claude.ai/cb"]}""")]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":" ","redirect_uris":["https://claude.ai/cb"]}""")]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude"}""")]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":[]}""")]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":[1]}""")]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":["/cb"]}""")]
    [InlineData("""["not","an","object"]""")]
    [InlineData("""not json""")]
    public void Name_and_redirects_are_required(string json) => Assert.Null(Parse(json));

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("private_key_jwt")]
    public void Confidential_auth_methods_are_refused(string method) =>
        Assert.Null(Parse($$"""
            {"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude",
             "redirect_uris":["https://claude.ai/cb"],"token_endpoint_auth_method":"{{method}}"}
            """));

    [Theory]
    [InlineData(null, 300)]
    [InlineData("no-store", 300)]
    [InlineData("max-age=60", 300)]
    [InlineData("max-age=3600", 3600)]
    [InlineData("public, max-age=604800", 86400)]
    public void Cache_lifetime_is_clamped_to_five_minutes_and_a_day(string? header, int seconds)
    {
        var document = Parse(
            """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":["https://claude.ai/cb"]}""",
            header is null ? null : CacheControlHeaderValue.Parse(header));

        Assert.Equal(TimeSpan.FromSeconds(seconds), document!.CacheFor);
    }
}
