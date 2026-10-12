using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Gateway;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed class GatewayHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Http { get; init; }
    public required string DataDir { get; init; }

    public string BaseUrl => App.Urls.First();

    public static async Task<GatewayHost> StartAsync(Action<WebApplicationBuilder>? configure = null)
    {
        var dataDir = Directory.CreateTempSubdirectory("ctx-ui-test-").FullName;
        var app = ServerApp.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["CtxUi:DataDir"] = dataDir;
            builder.Configuration["CtxUi:RefreshInterval"] = "00:00:00.050";
            configure?.Invoke(builder);
        });
        await ServerApp.InitializeAsync(app);
        await app.StartAsync();
        return new GatewayHost
        {
            App = app,
            DataDir = dataDir,
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) },
        };
    }

    public async Task<T> ArrangeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        var result = await ChangeAsync(action);
        await App.Services.GetRequiredService<SnapshotStore>().ReloadAsync();
        return result;
    }

    public async Task<T> ChangeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<Inbound> AddInboundAsync(
        string slug,
        string url,
        IEnumerable<KeyValuePair<string, string>>? headers = null,
        bool forwardIdentity = false) =>
        ArrangeAsync(services => services.GetRequiredService<InboundService>().AddHttpAsync(slug, null, url, headers, forwardIdentity));

    public Task<IssuedClient> AddClientAsync(
        string name,
        string[] slugs,
        DateTimeOffset? expiresAt = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null) =>
        ArrangeAsync(services => services.GetRequiredService<ClientService>().AddAsync(name, slugs, expiresAt, metadata));

    public static HttpRequestMessage Post(string path, string? bearer = null, string body = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
