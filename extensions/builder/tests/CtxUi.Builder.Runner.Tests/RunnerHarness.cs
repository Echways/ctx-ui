using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using CtxUi.Builder.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CtxUi.Builder.Runner.Tests;

internal static class Project
{
    public const string DefaultConfig = """
        {"source":{"path":"main.py","entrypoint":"mcp"},"environment":{"type":"uv","python":"3.13","dependencies":["fastmcp==4.1.4","httpx"]}}
        """;

    public static byte[] Archive(string? config = DefaultConfig, params (string Path, string Content)[] files)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var all = files.ToList();
            if (config is not null)
                all.Insert(0, ("fastmcp.json", config));

            foreach (var (path, content) in all)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                });
            }
        }

        return compressed.ToArray();
    }
}

internal sealed class FakeServerProbe : IServerProbe
{
    private readonly ConcurrentDictionary<string, string> _addresses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _healthy = new(StringComparer.Ordinal);

    public int Probes;

    public TaskCompletionSource? Gate { get; set; }

    public void Route(string container, string address) => _addresses[container] = address;

    public void SetHealthy(string container, bool healthy) => _healthy[container] = healthy;

    public string Address(string container) =>
        _addresses.TryGetValue(container, out var address) ? address : "http://127.0.0.1:9";

    public async Task<bool> IsHealthyAsync(string container, CancellationToken ct)
    {
        Interlocked.Increment(ref Probes);
        if (Gate is { } gate)
            await gate.Task;

        return _healthy.GetValueOrDefault(container, true);
    }
}

internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<HttpRequestRecord> _requests = new();

    public sealed record HttpRequestRecord(string Path, IReadOnlyDictionary<string, string> Headers);

    private FakeUpstream(WebApplication app) => _app = app;

    public string Name { get; private init; } = "";
    public string Address => $"http://127.0.0.1:{new Uri(_app.Urls.First()).Port}";
    public IReadOnlyList<HttpRequestRecord> Requests => _requests.ToArray();
    public TaskCompletionSource SecondEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task<FakeUpstream> StartAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var upstream = new FakeUpstream(app) { Name = name };
        app.MapFallback(async context =>
        {
            upstream._requests.Enqueue(new HttpRequestRecord(
                context.Request.Path,
                context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            if (context.Request.Query.ContainsKey("stream"))
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: first\n\n");
                await context.Response.Body.FlushAsync();
                await upstream.SecondEvent.Task;
                await context.Response.WriteAsync("data: second\n\n");
                return;
            }

            await context.Response.WriteAsync(name);
        });
        await app.StartAsync();
        return upstream;
    }

    public async ValueTask DisposeAsync()
    {
        SecondEvent.TrySetResult();
        await _app.DisposeAsync();
    }
}

internal sealed class RunnerHarness : IAsyncDisposable
{
    public const string Token = "control-token";
    public const string Secret = "route-secret";
    private const int PortAttempts = 5;
    public static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    public static readonly string Prefix = $"ctxb-{Id:N}";

    private readonly WebApplication _app;

    private RunnerHarness(WebApplication app, FakeDockerEngine docker, FakeServerProbe probe, FakeTimeProvider time, RunnerPorts ports)
    {
        _app = app;
        Docker = docker;
        Probe = probe;
        Time = time;
        Ports = ports;
        Control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ports.Control}") };
        Control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Ingress = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ports.Ingress}") };
    }

    public FakeDockerEngine Docker { get; }
    public FakeServerProbe Probe { get; }
    public FakeTimeProvider Time { get; }
    public RunnerPorts Ports { get; }
    public HttpClient Control { get; }
    public HttpClient Ingress { get; }
    public IServiceProvider Services => _app.Services;

    public static ServerSpec Spec(NetworkAccess access = NetworkAccess.InternetOnly, string secret = Secret) =>
        new(access, new ServerLimits(), secret);

    public static (WebApplication App, RunnerPorts Ports) Build(FakeDockerEngine docker, FakeServerProbe probe, TimeProvider time)
    {
        using var ingress = new TcpListener(IPAddress.Loopback, 0);
        using var control = new TcpListener(IPAddress.Loopback, 0);
        ingress.Start();
        control.Start();
        var ports = new RunnerPorts(((IPEndPoint)ingress.LocalEndpoint).Port, ((IPEndPoint)control.LocalEndpoint).Port);
        ingress.Stop();
        control.Stop();
        var options = new RunnerOptions(Token, "127.0.0.1", "runtime:1", "egress", "10.213.0.0/16", null, "runner-self");
        var app = RunnerEndpoints.BuildApp(
            ["--Logging:LogLevel:Default=None"],
            new RunnerDependencies(options, ports, docker, probe, time));
        return (app, ports);
    }

    public static async Task<RunnerHarness> StartAsync(FakeDockerEngine? docker = null, FakeServerProbe? probe = null, FakeTimeProvider? time = null)
    {
        docker ??= new FakeDockerEngine();
        probe ??= new FakeServerProbe();
        time ??= new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));
        for (var attempt = 1; ; attempt++)
        {
            var (app, ports) = Build(docker, probe, time);
            try
            {
                await app.StartAsync();
                return new RunnerHarness(app, docker, probe, time, ports);
            }
            catch (IOException) when (attempt < PortAttempts)
            {
                await app.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Control.Dispose();
        Ingress.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public async Task PutSpecAsync(ServerSpec? spec = null)
    {
        using var response = await Control.PutAsJsonAsync($"/v1/servers/{Id}", spec ?? Spec());
        response.EnsureSuccessStatusCode();
    }

    public Task<HttpResponseMessage> PostArchiveAsync(int release, byte[]? archive = null) =>
        Control.PostAsync($"/v1/servers/{Id}/releases/{release}", new ByteArrayContent(archive ?? Project.Archive()));

    public async Task<ReleaseStatus> StatusAsync(int release)
    {
        using var response = await Control.GetAsync($"/v1/servers/{Id}/releases/{release}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReleaseStatus>())!;
    }

    public async Task<ServerState> StateAsync()
    {
        using var response = await Control.GetAsync($"/v1/servers/{Id}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServerState>())!;
    }

    public async Task<ReleaseStatus> WaitForStateAsync(int release, ReleaseState state)
    {
        ReleaseStatus? last = null;
        await Eventually.TrueAsync(async () =>
        {
            last = await StatusAsync(release);
            return last.State == state;
        }, () => $"Release {release} is {last?.State}: {last?.Error}");
        return last!;
    }

    public async Task BuildAsync(int release, byte[]? archive = null)
    {
        using var response = await PostArchiveAsync(release, archive);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForStateAsync(release, ReleaseState.Built);
    }

    public async Task StartCandidateAsync(int release, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var response = await PostStartAsync(release, environment);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForStateAsync(release, ReleaseState.Candidate);
    }

    public Task<HttpResponseMessage> PostStartAsync(int release, IReadOnlyDictionary<string, string>? environment = null) =>
        Control.PostAsJsonAsync($"/v1/servers/{Id}/releases/{release}/start", new StartRelease(environment ?? new Dictionary<string, string>()));

    public Task<HttpResponseMessage> ActivateAsync(int release) =>
        Control.PostAsync($"/v1/servers/{Id}/releases/{release}/activate", null);

    public async Task PublishAsync(int release, byte[]? archive = null)
    {
        await BuildAsync(release, archive);
        await StartCandidateAsync(release);
        using var response = await ActivateAsync(release);
        response.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> CallAsync(string path, string? secret = Secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}") };
        if (secret is not null)
            request.Headers.Add(RouteSecret.Header, secret);

        return await Ingress.SendAsync(request);
    }

    public async Task<string> CallActiveAsync()
    {
        using var response = await CallAsync($"/s/{Id}/mcp");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

internal static class Eventually
{
    public static async Task TrueAsync(Func<Task<bool>> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail(describe?.Invoke() ?? "The condition did not become true in time.");
    }

    public static Task TrueAsync(Func<bool> condition, Func<string>? describe = null) =>
        TrueAsync(() => Task.FromResult(condition()), describe);
}
