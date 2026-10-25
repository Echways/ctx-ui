using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace CtxUi.IntegrationTests;

public sealed record TokenAnswer(HttpStatusCode Status, string? Access, string? Refresh, string? Error, JsonElement Body);

public sealed record PageForm(string Action, Dictionary<string, string> Fields);

public sealed partial class OAuthFlow : IDisposable
{
    public const string Callback = "http://127.0.0.1/callback";

    private const int RedirectLimit = 10;

    private readonly GatewayHost _gateway;

    private OAuthFlow(GatewayHost gateway, CookieContainer cookies)
    {
        _gateway = gateway;
        Cookies = cookies;
        Browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
        {
            BaseAddress = new Uri(gateway.BaseUrl),
        };
    }

    public HttpClient Browser { get; }

    public CookieContainer Cookies { get; }

    public string ClientId { get; private set; } = "";

    public string Resource { get; private set; } = "";

    public string Issuer { get; private set; } = "";

    public string AuthorizeUrl { get; private set; } = "";

    public string TokenEndpoint { get; private set; } = "";

    public string Verifier { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));

    public string State { get; } = Guid.NewGuid().ToString("N");

    public HttpStatusCode Status { get; private set; }

    public string Page { get; private set; } = "";

    public string? Location { get; private set; }

    public Dictionary<string, string?>? Answer { get; private set; }

    public string? Code => Answer?.GetValueOrDefault("code");

    public static async Task<OAuthFlow> StartAsync(
        GatewayHost gateway,
        string slug,
        CookieContainer? cookies = null,
        string clientName = "Claude Code",
        string redirectUri = Callback)
    {
        var flow = new OAuthFlow(gateway, cookies ?? new CookieContainer());
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/" + slug));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var metadataUrl = ResourceMetadata().Match(refused.Headers.WwwAuthenticate.ToString()).Groups[1].Value;
        var resource = await gateway.Http.GetFromJsonAsync<JsonElement>(metadataUrl);
        flow.Resource = resource.GetProperty("resource").GetString()!;
        flow.Issuer = resource.GetProperty("authorization_servers")[0].GetString()!;
        var server = await gateway.Http.GetFromJsonAsync<JsonElement>(flow.Issuer + "/.well-known/oauth-authorization-server");
        Assert.Equal(flow.Issuer, server.GetProperty("issuer").GetString());
        flow.TokenEndpoint = server.GetProperty("token_endpoint").GetString()!;

        using var registered = await gateway.Http.PostAsJsonAsync(
            server.GetProperty("registration_endpoint").GetString(),
            new Dictionary<string, object> { ["client_name"] = clientName, ["redirect_uris"] = new[] { redirectUri } });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        flow.ClientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;

        flow.AuthorizeUrl = QueryHelpers.AddQueryString(server.GetProperty("authorization_endpoint").GetString()!, new Dictionary<string, string?>
        {
            ["client_id"] = flow.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp offline_access",
            ["state"] = flow.State,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier))),
            ["code_challenge_method"] = "S256",
            ["resource"] = flow.Resource,
        });
        return flow;
    }

    public Task OpenAsync() => FollowAsync(new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl));

    public async Task SignInWithKeyAsync(string key)
    {
        await OpenAsync();
        if (Forms(Page).Any(form => form.Action == "/connect/signin"))
            await SubmitAsync("/connect/signin", new Dictionary<string, string> { ["key"] = key });
    }

    public async Task SwitchClientAsync(string key)
    {
        var authorize = new Uri(AuthorizeUrl).PathAndQuery;
        await FollowAsync(new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString("/connect/signin", "returnUrl", authorize)));
        await SubmitAsync("/connect/signin", new Dictionary<string, string> { ["key"] = key });
    }

    public Task NavigateAsync(string url) => FollowAsync(new HttpRequestMessage(HttpMethod.Get, url));

    public string LinkTo(string pathPrefix)
    {
        var match = Regex.Match(Page, "href=\"(?<href>" + Regex.Escape(pathPrefix) + "[^\"]*)\"");
        Assert.True(match.Success, $"no link to {pathPrefix} on the page:\n{Page}");
        return WebUtility.HtmlDecode(match.Groups["href"].Value);
    }

    public Task ApproveAsync() => Code is null ? SubmitAsync("/connect/consent") : Task.CompletedTask;

    public Task ReplaceAsync(Guid deviceId) =>
        SubmitAsync("/connect/limit/replace", new Dictionary<string, string> { ["deviceId"] = deviceId.ToString() }, deviceId.ToString());

    public async Task SubmitAsync(string action, IDictionary<string, string>? values = null, string? marker = null)
    {
        var form = Forms(Page).FirstOrDefault(form => form.Action == action && (marker is null || form.Fields.ContainsValue(marker)))
            ?? throw new InvalidOperationException($"no form posting to {action} on the page:\n{Page}");
        foreach (var (name, value) in values ?? new Dictionary<string, string>())
            form.Fields[name] = value;

        await FollowAsync(new HttpRequestMessage(HttpMethod.Post, form.Action) { Content = new FormUrlEncodedContent(form.Fields) });
    }

    public async Task<(string Access, string Refresh)> ExchangeAsync()
    {
        Assert.True(Code is not null, $"no authorization code, the browser stopped at {Status}:\n{Page}");
        var answer = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = Code!,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = Verifier,
        });
        Assert.True(answer.Status == HttpStatusCode.OK, answer.Body.ToString());
        return (answer.Access!, answer.Refresh!);
    }

    public Task<TokenAnswer> RefreshAsync(string refresh) => TokenAsync(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refresh,
    });

    public async Task<TokenAnswer> TokenAsync(Dictionary<string, string> form)
    {
        form["client_id"] = ClientId;
        form.TryAdd("resource", Resource);
        using var response = await _gateway.Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new TokenAnswer(
            response.StatusCode,
            body.TryGetProperty("access_token", out var access) ? access.GetString() : null,
            body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            body.TryGetProperty("error", out var error) ? error.GetString() : null,
            body);
    }

    public static IReadOnlyList<PageForm> Forms(string html) => FormPattern().Matches(html)
        .Select(form => new PageForm(
            WebUtility.HtmlDecode(form.Groups["action"].Value),
            InputPattern().Matches(form.Groups["body"].Value).ToDictionary(
                input => WebUtility.HtmlDecode(input.Groups["name"].Value),
                input => WebUtility.HtmlDecode(input.Groups["value"].Value))))
        .ToList();

    public static HttpRequestMessage Mcp(string slug, string bearer, string body = InitializeBody)
    {
        var request = GatewayHost.Post("/mcp/" + slug, bearer, body);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    public const string InitializeBody =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    public void Dispose() => Browser.Dispose();

    private async Task FollowAsync(HttpRequestMessage request)
    {
        Answer = null;
        for (var hop = 0; hop < RedirectLimit; hop++)
        {
            using (request)
            using (var response = await Browser.SendAsync(request))
            {
                Status = response.StatusCode;
                Page = await response.Content.ReadAsStringAsync();
                Location = response.Headers.Location?.OriginalString;
                if (Status is not (HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.SeeOther) || Location is null)
                    return;

                if (Location.StartsWith(Callback, StringComparison.Ordinal))
                {
                    Answer = QueryHelpers.ParseQuery(new Uri(Location).Query).ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString());
                    Assert.Equal(State, Answer.GetValueOrDefault("state"));
                    Assert.Equal(Issuer, Answer.GetValueOrDefault("iss"));
                    return;
                }
            }

            request = new HttpRequestMessage(HttpMethod.Get, Location);
        }

        throw new InvalidOperationException("too many redirects");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("resource_metadata=\"([^\"]+)\"")]
    private static partial Regex ResourceMetadata();

    [GeneratedRegex("<form[^>]*\\baction=\"(?<action>[^\"]*)\"[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline)]
    private static partial Regex FormPattern();

    [GeneratedRegex("<input[^>]*\\bname=\"(?<name>[^\"]*)\"[^>]*\\bvalue=\"(?<value>[^\"]*)\"", RegexOptions.Singleline)]
    private static partial Regex InputPattern();
}
