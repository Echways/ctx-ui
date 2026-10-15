using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.ApiKeys;

namespace CtxUi.IntegrationTests;

public sealed class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    private ApiClient(GatewayHost gateway, string? key)
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(gateway.PanelUrl) };
        if (key is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public static async Task<ApiClient> IssueAsync(GatewayHost gateway, string name = "tests") =>
        new(gateway, (await gateway.ChangeAsync(services => services.GetRequiredService<ApiKeyService>().AddAsync(name))).Secret);

    public static ApiClient WithKey(GatewayHost gateway, string? key) => new(gateway, key);

    public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);

    public Task<HttpResponseMessage> DeleteAsync(string path) => _http.DeleteAsync(path);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => _http.PostAsync(path, Content(body));

    public Task<HttpResponseMessage> PutAsync(string path, object body) => _http.PutAsync(path, Content(body));

    public Task<HttpResponseMessage> PatchAsync(string path, object body) => _http.PatchAsync(path, Content(body));

    public Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string body) =>
        _http.SendAsync(new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).Clone();

    public void Dispose() => _http.Dispose();

    private static JsonContent? Content(object? body) => body is null ? null : JsonContent.Create(body, options: Json);
}
