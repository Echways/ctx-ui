using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CtxUi.Builder.BlackBoxTests;

internal sealed class RunnerStack : IAsyncDisposable
{
    public const string ControlUrl = "http://127.0.0.1:8091";
    public const string IngressUrl = "http://127.0.0.1:8090";

    private const string Project = "ctxb-blackbox-test";
    private const string Tag = "0.0.0-blackbox-test";
    private const string ImagePrefix = "ghcr.io/echways/ctx-ui-builder-";
    private const string Label = "ctx-ui.builder";
    private const string ControlNetwork = "ctxb-control";

    private static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Guid> _servers = [];
    private bool _composed;
    private bool _started;

    public string Token { get; } = Guid.NewGuid().ToString("N");

    public static string ExtensionVersion
    {
        get
        {
            var manifest = Path.Combine(AppContext.BaseDirectory, "extensions", "builder", "extension.json");
            return JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("version").GetString()!;
        }
    }

    public IReadOnlyDictionary<string, string> PanelSettings => new Dictionary<string, string>
    {
        ["CtxUi:Extensions:builder:RunnerUrl"] = ControlUrl,
        ["CtxUi:Extensions:builder:RunnerIngressUrl"] = IngressUrl,
        ["CtxUi:Extensions:builder:RunnerToken"] = Token
    };

    public static string RuntimeImage => $"{ImagePrefix}runtime:{Tag}";

    public async Task EnsureStartedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_started)
                return;
            await RequireFreeAsync();
            await BuildImagesAsync();
            _composed = true;
            await Compose("up", "-d");
            await WaitUntilHealthyAsync();
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Track(Guid serverId)
    {
        _servers.Add(serverId);
    }

    public async Task RemoveServersAsync()
    {
        foreach (var id in _servers.ToArray())
        {
            await DeleteFromRunnerAsync(id);
            await RemoveByLabelAsync("ps", ["-a"], "rm", ["-f"], id);
            await RemoveByLabelAsync("network", ["ls"], "network", ["rm"], id);
            await RemoveByLabelAsync("volume", ["ls"], "volume", ["rm", "-f"], id);
            _servers.Remove(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RemoveServersAsync();
        if (_composed)
            await Shell.TryRunAsync("docker", ["compose", "-p", Project, "-f", ComposeFile, "down"],
                ComposeEnvironment);
        _gate.Dispose();
    }

    private static string ComposeFile => Path.Combine(BuilderEnvironment.Root, "deploy", "builder", "compose.yaml");

    private Dictionary<string, string> ComposeEnvironment =>
        new() { ["CTX_UI_VERSION"] = Tag, ["CTXB_RUNNER_TOKEN"] = Token };

    private Task<string> Compose(params string[] arguments)
    {
        return Shell.RunAsync("docker", ["compose", "-p", Project, "-f", ComposeFile, .. arguments],
            ComposeEnvironment);
    }

    private static async Task RequireFreeAsync()
    {
        foreach (var port in new[] { 8090, 8091 })
        {
            using var probe = new System.Net.Sockets.TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port);
            }
            catch (System.Net.Sockets.SocketException)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Port {port} on 127.0.0.1 is already in use, the builder flow tests need it for the Runner");
        }

        foreach (var (kind, name) in new[]
                     { ("container", "ctxb-runner"), ("container", "ctxb-egress"), ("network", ControlNetwork) })
        {
            var (code, _) = await Shell.TryRunAsync("docker", [kind, "inspect", name]);
            if (code == 0)
                throw new InvalidOperationException(
                    $"A {kind} named {name} already exists, the builder flow tests need its name for the Runner");
        }
    }

    private static async Task BuildImagesAsync()
    {
        var root = BuilderEnvironment.Root;
        var builder = Path.Combine(root, "extensions", "builder");
        await Shell.RunAsync("docker", ["build", "-q", "-t", RuntimeImage, Path.Combine(builder, "runtime")]);
        await Shell.RunAsync("docker",
            ["build", "-q", "-t", $"{ImagePrefix}egress:{Tag}", Path.Combine(builder, "egress")]);
        await Shell.RunAsync("docker",
        [
            "build", "-q", "-t", $"{ImagePrefix}runner:{Tag}", "--build-arg", $"VERSION={ExtensionVersion}", "-f",
            Path.Combine(builder, "CtxUi.Builder.Runner", "Dockerfile"), root
        ]);
    }

    private async Task WaitUntilHealthyAsync()
    {
        using var http = Authorized();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < StartPatience)
        {
            try
            {
                using var health = await http.GetAsync("/v1/health");
                if (health.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500);
        }

        var (_, log) = await Shell.TryRunAsync("docker", ["logs", "--tail", "30", "ctxb-runner"]);
        throw new TimeoutException($"The Runner did not answer in {StartPatience}: {log}");
    }

    private HttpClient Authorized()
    {
        var http = new HttpClient { BaseAddress = new Uri(ControlUrl), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return http;
    }

    private async Task DeleteFromRunnerAsync(Guid id)
    {
        if (!_started)
            return;
        try
        {
            using var http = Authorized();
            using var deleted = await http.DeleteAsync($"/v1/servers/{id}");
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
        }
    }

    private static async Task RemoveByLabelAsync(string listKind, string[] listFlags, string removeKind,
        string[] removeFlags, Guid id)
    {
        var (_, listed) =
            await Shell.TryRunAsync("docker", [listKind, .. listFlags, "-q", "--filter", $"label={Label}={id}"]);
        var identifiers = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (identifiers.Length > 0)
            await Shell.TryRunAsync("docker", [removeKind, .. removeFlags, .. identifiers]);
    }
}
