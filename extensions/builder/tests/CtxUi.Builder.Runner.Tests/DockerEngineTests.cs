using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using CtxUi.Builder.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CtxUi.Builder.Runner.Tests;

internal sealed partial class StubDockerApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _calls = new();

    public sealed record Connect(string Network, JsonElement Body);

    public ConcurrentQueue<Connect> Connects { get; } = new();
    public Dictionary<string, Dictionary<string, string>> Containers { get; } = [];
    public Dictionary<string, Dictionary<string, string>> Volumes { get; } = [];
    public Dictionary<string, Dictionary<string, string>> Networks { get; } = [];

    public IReadOnlyList<string> Calls => _calls.ToArray();

    public IReadOnlyList<string> Mutations =>
        _calls.Where(call => !call.StartsWith("GET ", StringComparison.Ordinal)).ToList();

    public Uri Address => new(_app.Urls.First());

    private StubDockerApi(WebApplication app) => _app = app;

    public static async Task<StubDockerApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubDockerApi(app);
        app.Run(stub.HandleAsync);
        await app.StartAsync();
        return stub;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var path = VersionPrefix().Replace(context.Request.Path.Value!, "");
        var method = context.Request.Method;
        _calls.Enqueue($"{method} {path}");

        var inspect = InspectPath().Match(path);
        if (method == "GET" && inspect.Success)
        {
            var name = inspect.Groups["name"].Value;
            await WriteLabelled(context, Containers, name, labels => new
            {
                Id = name,
                Name = "/" + name,
                Config = new { Labels = labels },
                State = new { Running = true },
                NetworkSettings = new { Networks = new Dictionary<string, object>() }
            });
            return;
        }

        var volume = VolumePath().Match(path);
        if (method == "GET" && volume.Success)
        {
            var name = volume.Groups["name"].Value;
            await WriteLabelled(context, Volumes, name, labels => new { Name = name, Labels = labels });
            return;
        }

        var connect = ConnectPath().Match(path);
        if (method == "POST" && connect.Success)
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            Connects.Enqueue(new Connect(connect.Groups["name"].Value, body.RootElement.Clone()));
            return;
        }

        var network = NetworkPath().Match(path);
        if (method == "GET" && network.Success)
        {
            var name = network.Groups["name"].Value;
            await WriteLabelled(context, Networks, name, labels => new { Name = name, Id = name, Labels = labels });
            return;
        }

        if (method == "POST" && path == "/containers/create")
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new { Id = "created" });
            return;
        }

        if (method == "POST" && path == "/volumes/create")
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new { Name = "created" });
            return;
        }

        if (method == "POST" && path == "/networks/create")
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsJsonAsync(new { Id = "created" });
            return;
        }

        if (method == "POST" && path.EndsWith("/wait", StringComparison.Ordinal))
        {
            await context.Response.WriteAsJsonAsync(new { StatusCode = 0 });
            return;
        }

        context.Response.StatusCode = method == "DELETE" ? StatusCodes.Status204NoContent : StatusCodes.Status200OK;
    }

    private static async Task WriteLabelled<T>(HttpContext context, Dictionary<string, Dictionary<string, string>> objects, string name, Func<Dictionary<string, string>, T> shape)
    {
        if (!objects.TryGetValue(name, out var labels))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { message = "not found" });
            return;
        }

        await context.Response.WriteAsJsonAsync(shape(labels));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [GeneratedRegex(@"^/v\d+\.\d+")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"^/containers/(?<name>[^/]+)/json$")]
    private static partial Regex InspectPath();

    [GeneratedRegex(@"^/volumes/(?<name>[^/]+)$")]
    private static partial Regex VolumePath();

    [GeneratedRegex(@"^/networks/(?<name>[^/]+)/connect$")]
    private static partial Regex ConnectPath();

    [GeneratedRegex(@"^/networks/(?<name>[^/]+)$")]
    private static partial Regex NetworkPath();
}

public sealed class DockerEngineTests : IAsyncLifetime
{
    private static readonly Guid Server = RunnerHarness.Id;
    private static readonly Guid Other = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private StubDockerApi _api = null!;
    private DockerEngine _engine = null!;

    public async ValueTask InitializeAsync()
    {
        _api = await StubDockerApi.StartAsync();
        _engine = new DockerEngine(_api.Address);
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Dispose();
        await _api.DisposeAsync();
    }

    private static Dictionary<string, string> Owned(string owner) => new() { [BuilderLabels.Root] = owner };

    public static TheoryData<string> ForeignLabels => new()
    {
        "unlabelled",
        "other-server",
        "system"
    };

    private void Seed()
    {
        _api.Containers["unlabelled"] = [];
        _api.Containers["other-server"] = Owned(Other.ToString());
        _api.Containers["system"] = Owned(BuilderLabels.SystemValue);
        _api.Containers["mine"] = Owned(Server.ToString());
        _api.Volumes["unlabelled"] = [];
        _api.Volumes["other-server"] = Owned(Other.ToString());
        _api.Volumes["system"] = Owned(BuilderLabels.SystemValue);
        _api.Volumes["mine"] = Owned(Server.ToString());
        _api.Networks["unlabelled"] = [];
        _api.Networks["other-server"] = Owned(Other.ToString());
        _api.Networks["system"] = Owned(BuilderLabels.SystemValue);
        _api.Networks["mine"] = Owned(Server.ToString());
    }

    [Theory]
    [MemberData(nameof(ForeignLabels))]
    public async Task Objects_without_the_builder_label_are_never_touched(string name)
    {
        Seed();
        var ct = CancellationToken.None;
        Func<Task>[] operations =
        [
            () => _engine.StartContainerAsync(Server, name, ct),
            () => _engine.StopContainerAsync(Server, name, ct),
            () => _engine.RemoveContainerAsync(Server, name, ct),
            () => _engine.WaitContainerAsync(Server, name, ct),
            () => _engine.ExtractArchiveAsync(Server, name, "/code", new MemoryStream([1]), ct),
            () => _engine.ReadFileAsync(Server, name, "/env/requirements.lock", ct),
            () => _engine.RemoveVolumeAsync(Server, name, ct),
            () => _engine.RemoveNetworkAsync(Server, name, ct),
            () => _engine.NetworkExistsAsync(Server, name, ct),
            () => _engine.EnsureVolumeAsync(Server, new VolumesCreateParameters { Name = name, Labels = Owned(Server.ToString()) }, ct),
            async () =>
            {
                await foreach (var _ in _engine.ReadLogsAsync(Server, name, null, false, ct))
                {
                }
            }
        ];

        foreach (var operation in operations)
        {
            await Assert.ThrowsAsync<BuilderObjectRefusedException>(operation);
        }

        Assert.Empty(_api.Mutations);
    }

    [Theory]
    [InlineData("unlabelled", "mine")]
    [InlineData("other-server", "mine")]
    [InlineData("mine", "unlabelled")]
    [InlineData("mine", "other-server")]
    [InlineData("mine", "system")]
    [InlineData("system", "other-server")]
    public async Task Connect_refuses_a_foreign_container_or_network(string container, string network)
    {
        Seed();

        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.ConnectAsync(Server, network, container, CancellationToken.None));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.DisconnectAsync(Server, network, container, CancellationToken.None));

        Assert.Empty(_api.Mutations);
    }

    [Theory]
    [InlineData("mine")]
    [InlineData("system")]
    public async Task Connect_allows_this_servers_and_system_containers_and_never_takes_the_default_route(string container)
    {
        Seed();

        await _engine.ConnectAsync(Server, "mine", container, CancellationToken.None);

        var connect = Assert.Single(_api.Connects);
        Assert.Equal("mine", connect.Network);
        Assert.Equal(container, connect.Body.GetProperty("Container").GetString());
        Assert.True(connect.Body.GetProperty("EndpointConfig").GetProperty("GwPriority").GetInt32() < 0);
    }

    [Fact]
    public async Task Objects_of_this_server_are_acted_on()
    {
        Seed();
        var ct = CancellationToken.None;

        await _engine.StartContainerAsync(Server, "mine", ct);
        await _engine.StopContainerAsync(Server, "mine", ct);
        await _engine.RemoveContainerAsync(Server, "mine", ct);
        await _engine.RemoveVolumeAsync(Server, "mine", ct);
        await _engine.RemoveNetworkAsync(Server, "mine", ct);

        Assert.Contains("POST /containers/mine/start", _api.Mutations);
        Assert.Contains("POST /containers/mine/stop", _api.Mutations);
        Assert.Contains("DELETE /containers/mine", _api.Mutations);
        Assert.Contains("DELETE /volumes/mine", _api.Mutations);
        Assert.Contains("DELETE /networks/mine", _api.Mutations);
    }

    [Fact]
    public async Task Removing_what_is_already_gone_is_not_an_error()
    {
        var ct = CancellationToken.None;

        await _engine.RemoveContainerAsync(Server, "gone", ct);
        await _engine.RemoveVolumeAsync(Server, "gone", ct);
        await _engine.RemoveNetworkAsync(Server, "gone", ct);

        Assert.Empty(_api.Mutations);
    }

    [Fact]
    public async Task Created_objects_must_carry_this_servers_label()
    {
        var ct = CancellationToken.None;

        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.CreateContainerAsync(
            Server, new CreateContainerParameters { Name = "x", Labels = Owned(Other.ToString()) }, ct));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.CreateNetworkAsync(
            Server, new NetworksCreateParameters { Name = "x", Labels = new Dictionary<string, string>() }, ct));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.EnsureVolumeAsync(
            Server, new VolumesCreateParameters { Name = "x", Labels = Owned(BuilderLabels.SystemValue) }, ct));

        Assert.Empty(_api.Mutations);
    }

    [Fact]
    public async Task Existing_volume_of_this_server_is_reused_and_a_new_one_is_created()
    {
        Seed();
        var ct = CancellationToken.None;

        await _engine.EnsureVolumeAsync(Server, new VolumesCreateParameters { Name = "mine", Labels = Owned(Server.ToString()) }, ct);
        Assert.Empty(_api.Mutations);

        await _engine.EnsureVolumeAsync(Server, new VolumesCreateParameters { Name = "new", Labels = Owned(Server.ToString()) }, ct);
        Assert.Contains("POST /volumes/create", _api.Mutations);
    }

    [Fact]
    public async Task System_container_check_accepts_only_the_system_label()
    {
        Seed();
        var ct = CancellationToken.None;

        Assert.True(await _engine.RequireSystemContainerAsync("system", ct));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.RequireSystemContainerAsync("mine", ct));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.RequireSystemContainerAsync("unlabelled", ct));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => _engine.RequireSystemContainerAsync("gone", ct));
    }
}
