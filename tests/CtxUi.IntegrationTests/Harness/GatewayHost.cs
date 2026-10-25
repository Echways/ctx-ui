using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Settings;
using CtxUi.Gateway;
using CtxUi.Panel.Accounts;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed class GatewayHost : IAsyncDisposable
{
    private readonly Action<WebApplicationBuilder>? _configure;
    private readonly int _port;

    private GatewayHost(string dataDir, Action<WebApplicationBuilder>? configure, int port)
    {
        DataDir = dataDir;
        _configure = configure;
        _port = port;
    }

    public WebApplication App { get; private set; } = null!;

    public HttpClient Http { get; private set; } = null!;

    public string DataDir { get; }

    public string BaseUrl => App.Urls.First();

    public string PanelPath { get; private set; } = "";

    public string PanelUrl => $"{BaseUrl}/{PanelPath}/";

    public string Issuer => App.Services.GetRequiredService<IOptions<CtxUiOptions>>().Value.PublicUrl.TrimEnd('/');

    public static async Task<GatewayHost> StartAsync(Action<WebApplicationBuilder>? configure = null, int port = 0)
    {
        var host = new GatewayHost(Directory.CreateTempSubdirectory("ctx-ui-test-").FullName, configure, port);
        await host.BootAsync();
        return host;
    }

    public async Task RestartAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await BootAsync();
    }

    public async Task<T> ArrangeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        var result = await ChangeAsync(action);
        await App.Services.GetRequiredService<SnapshotStore>().ReloadAsync();
        return result;
    }

    public Task ArrangeAsync(Func<IServiceProvider, Task> action) => ArrangeAsync(async services =>
    {
        await action(services);
        return true;
    });

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
        IEnumerable<KeyValuePair<string, string>>? metadata = null,
        ClientLimits? limits = null) =>
        ArrangeAsync(services => services.GetRequiredService<ClientService>().AddAsync(name, slugs, expiresAt, metadata, limits));

    public Task<IssuedAdmin> AddAdminAsync(string name = "root") =>
        ChangeAsync(services => services.GetRequiredService<AdminAccounts>().AddAsync(name));

    public Task SetLimitsAsync(string name, ClientLimits limits) => ArrangeAsync(async services =>
    {
        var clients = services.GetRequiredService<ClientService>();
        await clients.UpdateAsync((await clients.GetAsync(name)).Id, new ClientUpdate { Limits = limits });
        return true;
    });

    public async Task FlushUsageAsync()
    {
        await App.Services.GetRequiredService<ClientTouch>().FlushAsync();
        await App.Services.GetRequiredService<UsageJournal>().FlushAsync();
    }

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

    private async Task BootAsync()
    {
        var app = ServerApp.Build([], builder =>
        {
            builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");
            builder.Configuration["CtxUi:DataDir"] = DataDir;
            builder.Configuration["CtxUi:RefreshInterval"] = "00:00:00.050";
            builder.Configuration["CtxUi:FlushInterval"] = "00:00:00.050";
            builder.Configuration["CtxUi:Language"] = "ru";
            _configure?.Invoke(builder);
        });
        await ServerApp.InitializeAsync(app);
        await app.StartAsync();
        App = app;
        Http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        PanelPath = await ChangeAsync(services => services.GetRequiredService<PanelSettings>().PathAsync());
    }
}
