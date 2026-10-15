using System.Net;
using System.Text.RegularExpressions;

namespace CtxUi.IntegrationTests;

public sealed partial class PanelClient : IDisposable
{
    private readonly HttpClient _http;

    public PanelClient(GatewayHost gateway, string? forwardedProto = null)
    {
        Root = "/" + gateway.PanelPath;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
        {
            BaseAddress = new Uri(gateway.BaseUrl),
        };
        if (forwardedProto is not null)
            _http.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);
    }

    public string Root { get; }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

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
