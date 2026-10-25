using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CtxUi.IntegrationTests;

public class OAuthMcpClientTests
{
    private static async Task<AuthorizationResult?> SignInAsync(HttpClient browser, Uri authorize, string key)
    {
        var response = await browser.GetAsync(authorize);
        for (var hop = 0; hop < 10; hop++)
        {
            var location = response.Headers.Location?.OriginalString;
            if (location is not null && location.StartsWith(OAuthFlow.Callback, StringComparison.Ordinal))
            {
                var answer = QueryHelpers.ParseQuery(new Uri(location).Query);
                response.Dispose();
                return new AuthorizationResult { Code = answer["code"], State = answer["state"], Iss = answer["iss"] };
            }

            if (location is not null)
            {
                response.Dispose();
                response = await browser.GetAsync(location);
                continue;
            }

            var page = await response.Content.ReadAsStringAsync();
            response.Dispose();
            var forms = OAuthFlow.Forms(page);
            var form = forms.FirstOrDefault(form => form.Action == "/connect/signin") is { } signIn
                ? signIn
                : forms.Single(form => form.Action == "/connect/consent");
            if (form.Action == "/connect/signin")
                form.Fields["key"] = key;

            response = await browser.PostAsync(form.Action, new FormUrlEncodedContent(form.Fields));
        }

        response.Dispose();
        return null;
    }

    [Fact]
    public async Task Mcp_sdk_client_connects_through_oauth()
    {
        await using var server = await McpEndToEndTests.StartMcpServerAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(gateway.BaseUrl),
        };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(gateway.BaseUrl + "/mcp/echo"),
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri(OAuthFlow.Callback),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "MCP SDK" },
                AuthorizationCallbackHandler = (context, _) => SignInAsync(browser, context.AuthorizationUri, issued.Token),
            },
        });

        await using var client = await McpClient.CreateAsync(transport);
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "привет" });

        Assert.Equal("echo: привет", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }
}
