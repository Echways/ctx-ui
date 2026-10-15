using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;

namespace CtxUi.IntegrationTests;

public sealed partial class PanelClient : IDisposable
{
    private readonly HttpClient _http;

    public PanelClient(GatewayHost gateway, string? forwardedProto = null)
    {
        Root = "/" + gateway.PanelPath;
        var cookies = new CookieContainer();
        cookies.Add(new Uri(gateway.PanelUrl), new Cookie(
            CookieRequestCultureProvider.DefaultCookieName, CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("ru"))));
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
        {
            BaseAddress = new Uri(gateway.BaseUrl),
        };
        if (forwardedProto is not null)
            _http.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
    }

    public string Root { get; }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    public static async Task<string> PageAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    public Task<HttpResponseMessage> GetAsync(string page) => _http.GetAsync($"{Root}/{page}");

    public async Task<HttpResponseMessage> PostAsync(string page, Dictionary<string, string> fields, bool withToken = true)
    {
        if (withToken)
        {
            using var form = await GetAsync(page);
            fields["__RequestVerificationToken"] = TokenField().Match(await form.Content.ReadAsStringAsync()).Groups[1].Value;
        }

        return await _http.PostAsync($"{Root}/{page}", new FormUrlEncodedContent(fields));
    }

    public Task<HttpResponseMessage> SignInAsync(string name, string password, string query = "") => PostAsync(
        "login" + query,
        new Dictionary<string, string> { ["_handler"] = "login", ["Input.Name"] = name, ["Input.Password"] = password });

    public Task<HttpResponseMessage> SignOutAsync() =>
        PostAsync("logout", new Dictionary<string, string> { ["_handler"] = "logout" });

    public void Dispose() => _http.Dispose();
}
