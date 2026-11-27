using System.Net.Http.Headers;
using System.Text;
using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class ClientMetadataDocumentTests
{
    private static readonly Uri Url = new("https://claude.ai/oauth/mcp-oauth-client-metadata");

    private static ClientMetadataDocument? Parse(string json, CacheControlHeaderValue? cache = null, Uri? url = null)
    {
        return ClientMetadataDocument.Parse(url ?? Url, Encoding.UTF8.GetBytes(json), cache);
    }

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
    [InlineData("AxUDVSyHSHFa/client.json", "connector/oauth/AxUDVSyHSHFa", "")]
    [InlineData("client.json", "connector_platform_oauth_redirect", ",\"urn:ietf:params:oauth:grant-type:jwt-bearer\"")]
    public void ChatGpt_document_selects_public_authentication(string path, string callback, string extraGrant)
    {
        var url = new Uri("https://chatgpt.com/oauth/" + path);
        var document = Parse($$"""
            {
              "client_id": "{{url.OriginalString}}",
              "client_uri": "https://chatgpt.com/",
              "redirect_uris": ["https://chatgpt.com/{{callback}}"],
              "token_endpoint_auth_method": "private_key_jwt",
              "token_endpoint_auth_methods_supported": ["none", "private_key_jwt"],
              "grant_types": ["authorization_code", "refresh_token"{{extraGrant}}],
              "response_types": ["code"],
              "client_name": "ChatGPT",
              "logo_uri": "https://persistent.oaistatic.com/sonic/misc/openai-logo.png",
              "token_endpoint_auth_signing_alg": "RS256",
              "jwks_uri": "https://chatgpt.com/oauth/jwks.json"
            }
            """, url: url);

        Assert.NotNull(document);
        Assert.Equal("none", document.TokenEndpointAuthMethod);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(""", "token_endpoint_auth_method":"none" """, true)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["none"] """, true)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["other","none","none"] """, true)]
    [InlineData(""", "token_endpoint_auth_method":"other","token_endpoint_auth_methods_supported":["none","other"] """, true)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["private_key_jwt"] """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["NONE","unknown"] """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["none","client_secret_basic"] """, false)]
    [InlineData(""", "token_endpoint_auth_method":"private_key_jwt","token_endpoint_auth_methods_supported":["none"] """, false)]
    [InlineData(""", "token_endpoint_auth_method":"none","token_endpoint_auth_methods_supported":["private_key_jwt"] """, false)]
    [InlineData(""", "token_endpoint_auth_method":null,"token_endpoint_auth_methods_supported":["none"] """, false)]
    [InlineData(""", "token_endpoint_auth_method":42 """, false)]
    [InlineData(""", "token_endpoint_auth_method":" " """, false)]
    [InlineData(""", "token_endpoint_auth_method":"private_key_jwt","token_endpoint_auth_method":"none" """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":null """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":"none" """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":{} """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":[] """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["none", null] """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["none", 42] """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["none", ""] """, false)]
    [InlineData(""", "token_endpoint_auth_methods_supported":["none", "\uD800"] """, false)]
    [InlineData(""", "grant_types":["client_credentials"] """, false)]
    [InlineData(""", "response_types":["token"] """, false)]
    [InlineData(""", "grant_types":["authorization_code"],"response_types":["code","token"] """, true)]
    public void Client_metadata_is_validated_before_selecting_public_authentication(string metadata, bool accepted)
    {
        var document = Parse($$"""
            {"client_id":"{{Url.OriginalString}}","client_name":"Claude","redirect_uris":["https://claude.ai/cb"]{{metadata}}}
            """);

        if (accepted)
            Assert.Equal("none", document?.TokenEndpointAuthMethod);
        else
            Assert.Null(document);
    }

    [Theory]
    [InlineData("http://claude.ai/cb")]
    [InlineData("https://claude.ai/cb#fragment")]
    [InlineData("javascript:alert(1)")]
    public void Unsafe_redirect_uris_are_refused(string redirect)
    {
        Assert.Null(Parse($$"""
            {"client_id":"{{Url.OriginalString}}","client_name":"Claude","redirect_uris":["{{redirect}}"]}
            """));
    }

    [Theory]
    [InlineData("https://claude.ai/oauth/mcp-oauth-client-metadata/")]
    [InlineData("https://CLAUDE.ai/oauth/mcp-oauth-client-metadata")]
    [InlineData("https://claude.ai/oauth/other")]
    public void Client_id_must_match_the_url_exactly(string clientId)
    {
        Assert.Null(Parse(
            $$"""{"client_id":"{{clientId}}","client_name":"Claude","redirect_uris":["https://claude.ai/cb"]}"""));
    }

    [Theory]
    [InlineData(
        """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","redirect_uris":["https://claude.ai/cb"]}""")]
    [InlineData(
        """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":" ","redirect_uris":["https://claude.ai/cb"]}""")]
    [InlineData("""{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude"}""")]
    [InlineData(
        """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":[]}""")]
    [InlineData(
        """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":[1]}""")]
    [InlineData(
        """{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","redirect_uris":["/cb"]}""")]
    [InlineData("""["not","an","object"]""")]
    [InlineData("""not json""")]
    public void Name_and_redirects_are_required(string json)
    {
        Assert.Null(Parse(json));
    }

    [Theory]
    [InlineData("client_secret_basic")]
    [InlineData("client_secret_post")]
    [InlineData("private_key_jwt")]
    public void Confidential_auth_methods_are_refused(string method)
    {
        Assert.Null(Parse($$"""
                            {"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude",
                             "redirect_uris":["https://claude.ai/cb"],"token_endpoint_auth_method":"{{method}}"}
                            """));
    }

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
