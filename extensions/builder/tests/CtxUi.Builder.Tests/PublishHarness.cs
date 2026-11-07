using System.Text.Json;
using System.Text.Json.Nodes;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using CtxUi.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CtxUi.Builder.Tests;

public sealed class FakeInspector : IToolInspector
{
    public Func<int?, IReadOnlyList<string>> Names { get; set; } = _ => ["alpha"];

    public Exception? Failure { get; set; }

    public bool Hang { get; set; }

    public List<int?> Listed { get; } = [];

    public List<int?> Called { get; } = [];

    public async Task<IReadOnlyList<ToolInfo>> ListAsync(Guid serverId, int? release, CancellationToken ct)
    {
        Listed.Add(release);
        if (Hang)
            await Task.Delay(Timeout.Infinite, ct);
        if (Failure is not null)
            throw Failure;
        return [.. Names(release).Select(name => new ToolInfo(name, $"{name} tool", JsonDocument.Parse("{}").RootElement.Clone()))];
    }

    public Task<ToolCallOutcome> CallAsync(Guid serverId, int? release, string tool, JsonObject arguments, CancellationToken ct)
    {
        Called.Add(release);
        return Failure is not null ? Task.FromException<ToolCallOutcome>(Failure) : Task.FromResult(new ToolCallOutcome(false, "ok", TimeSpan.Zero, false));
    }
}

public sealed class PublishHarness : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private ServiceProvider _services;
    private readonly bool _configured;
    private bool _started;

    private PublishHarness(bool configured)
    {
        Directory.CreateDirectory(_directory);
        _configured = configured;
        _services = Build(configured);
    }

    public FakeRunner Runner { get; } = new();

    public FakeInspector Inspector { get; } = new();

    public FakePanelData Data { get; } = new();

    public ConnectedInbounds Connected { get; } = new();

    public SecretBox Box { get; } = new(new EphemeralDataProtectionProvider());

    public string DataDirectory => _directory;

    public IServiceProvider Services => _services;

    public TimeSpan RepairInterval { get; set; } = TimeSpan.FromHours(1);

    public PublishQueue Queue => _services.GetRequiredService<PublishQueue>();

    public ReleaseArchives Archives => _services.GetRequiredService<ReleaseArchives>();

    public static async Task<PublishHarness> CreateAsync(bool configured = true)
    {
        var harness = new PublishHarness(configured);
        await using var scope = harness._services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BuilderDatabase>().Database.MigrateAsync();
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
            await Queue.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    public async Task RestartPanelAsync()
    {
        await Queue.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _services = Build(_configured);
        await Queue.InitializeAsync(default);
        await StartAsync();
    }

    public async Task StartAsync()
    {
        _started = true;
        await Queue.StartAsync(CancellationToken.None);
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task<Guid> CreateServerAsync(string slug = "weather")
    {
        var server = await WithScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<ServerCatalog>();
            var created = await catalog.CreateAsync(slug, slug, default);
            await catalog.SaveFileAsync(created.Id, "mcp/hello.py", "from fastmcp.tools import tool\n\n\n@tool\ndef hello() -> str:\n    return 'hi'\n", 0, default);
            return created;
        });
        return server.Id;
    }

    public Task<BuilderServer> ServerAsync(Guid id) =>
        WithScopeAsync(services => services.GetRequiredService<BuilderDatabase>().Servers.AsNoTracking().SingleAsync(server => server.Id == id));

    public Task<Release> ReleaseAsync(Guid serverId, int number) =>
        WithScopeAsync(services => services.GetRequiredService<BuilderDatabase>().Releases.AsNoTracking().SingleAsync(release => release.ServerId == serverId && release.Number == number));

    public Task<List<Release>> ReleasesAsync(Guid serverId) =>
        WithScopeAsync(services => services.GetRequiredService<BuilderDatabase>().Releases.AsNoTracking().Where(release => release.ServerId == serverId).OrderBy(release => release.Number).ToListAsync());

    public async Task<int> PublishAndWaitAsync(Guid serverId, PublishMode mode = PublishMode.Activate)
    {
        var number = await Queue.PublishAsync(serverId, mode, default);
        await WaitForAsync(async () => (await ReleaseAsync(serverId, number)).Status is not ReleaseState.Building);
        return number;
    }

    public async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    public Task WaitIdleAsync(Guid serverId) =>
        WaitForAsync(async () => !(await WithScopeAsync(services => Task.FromResult(services.GetRequiredService<PublishQueue>().IsBusy(serverId)))));

    public ToolInspector CreateInspector(string ingressUrl) => new(
        new RunnerSettings("http://runner:8091", ingressUrl, "token"),
        _services.GetRequiredService<IHttpClientFactory>(),
        _services.GetRequiredService<ServerCatalog>());

    public Task<InboundSummary> ConnectAsync(Guid serverId, string slug, string name = "Weather", bool forwardIdentity = false, params Guid[] clients) =>
        WithScopeAsync(services => services.GetRequiredService<ConnectionService>().ConnectAsync(serverId, slug, name, forwardIdentity, clients, default));

    public Task<InboundSummary?> FindInboundAsync(Guid serverId) =>
        WithScopeAsync(async services => await services.GetRequiredService<ConnectionService>().FindInboundAsync(await ServerAsync(serverId), default));

    public Task DeleteServerAsync(Guid serverId) =>
        WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ConnectionService>().DeleteServerAsync(serverId, default);
            return true;
        });

    public Task<bool> ServerExistsAsync(Guid serverId) =>
        WithScopeAsync(services => services.GetRequiredService<BuilderDatabase>().Servers.AsNoTracking().AnyAsync(server => server.Id == serverId));

    public Task SetInboundIdAsync(Guid serverId, string? inboundId) =>
        WithScopeAsync(async services =>
        {
            await services.GetRequiredService<BuilderDatabase>().Servers.Where(server => server.Id == serverId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(server => server.InboundId, inboundId));
            return true;
        });

    public Task RemoveServerRowAsync(Guid serverId) =>
        WithScopeAsync(async services =>
        {
            await services.GetRequiredService<BuilderDatabase>().Servers.Where(server => server.Id == serverId).ExecuteDeleteAsync();
            return true;
        });

    public Task<string> RouteSecretAsync(Guid id) =>
        WithScopeAsync(services => services.GetRequiredService<ServerCatalog>().RouteSecretAsync(id, default));

    public async Task<ServerSettingsView> SettingsAsync(Guid id) =>
        await WithScopeAsync(services => services.GetRequiredService<ServerCatalog>().SettingsAsync(id, default));

    private ServiceProvider Build(bool configured)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<BuilderDatabase>(options => options.UseSqlite($"Data Source={Path.Combine(_directory, "data.db")}"));
        services.AddSingleton(Box);
        services.AddSingleton<ServerCatalog>();
        services.AddSingleton(configured
            ? new RunnerSettings("http://runner:8091", "http://runner:8090", "token")
            : new RunnerSettings(null, null, null));
        services.AddTransient(_ => new RunnerClient(new HttpClient(Runner, false) { BaseAddress = new Uri("http://runner:8091/") }));
        services.AddPublishing(_directory);
        services.AddSingleton(_ => new PublishTimings(TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), RepairInterval));
        services.AddSingleton<IToolInspector>(Inspector);
        services.AddSingleton<IPanelData>(Data);
        services.AddSingleton(Connected);
        services.AddScoped<ConnectionService>();
        return services.BuildServiceProvider();
    }
}
