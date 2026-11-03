using System.Globalization;
using System.Runtime.CompilerServices;
using Docker.DotNet.Models;

namespace CtxUi.Builder.Runner.Tests;

internal sealed class FakeContainer(CreateContainerParameters parameters)
{
    public CreateContainerParameters Parameters { get; } = parameters;
    public string Name => Parameters.Name;
    public IDictionary<string, string> Labels => Parameters.Labels;
    public bool Running { get; set; }
    public string? Role => Labels.TryGetValue(BuilderLabels.Role, out var role) ? role : null;
    public int? Release => Labels.TryGetValue(BuilderLabels.Release, out var release) ? int.Parse(release, CultureInfo.InvariantCulture) : null;

    public string? MountSource(string target) =>
        Parameters.HostConfig.Mounts.FirstOrDefault(mount => mount.Target == target)?.Source;
}

internal sealed class FakeDockerEngine : IDockerEngine
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FakeContainer> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDictionary<string, string>> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetworksCreateParameters> _networks = new(StringComparer.Ordinal);
    private readonly HashSet<(string Network, string Container)> _attachments = [];
    private readonly HashSet<string> _completedEnvironments = new(StringComparer.Ordinal);
    private readonly List<string> _created = [];
    private readonly List<string> _removedNetworks = [];
    private readonly List<Extraction> _extractions = [];

    public sealed record Extraction(string Container, string Path, byte[] Archive, bool ContainerRunning);

    public string EngineVersion { get; set; } = "29.0.0";
    public long BuildExitCode { get; set; }
    public string BuildLog { get; set; } = "installing packages\n";
    public string ServerLog { get; set; } = "server started\n";
    public string LockFile { get; set; } = "fastmcp==4.1.4\n";
    public TaskCompletionSource? BuildGate { get; set; }
    public Func<string, Exception?>? BeforeCreate { get; set; }
    public Func<string, Exception?>? AfterCreate { get; set; }
    public Func<string, Exception?>? BeforeRemove { get; set; }
    public Func<string, TaskCompletionSource?>? AfterCreateGate { get; set; }
    public HashSet<string> SystemContainers { get; } = new(StringComparer.Ordinal) { "egress", "runner-self" };
    public IReadOnlyList<string> Subnets { get; set; } = [];

    public IReadOnlyList<string> Created { get { lock (_sync) { return _created.ToList(); } } }
    public IReadOnlyList<string> RemovedNetworks { get { lock (_sync) { return _removedNetworks.ToList(); } } }
    public IReadOnlyList<Extraction> Extractions { get { lock (_sync) { return _extractions.ToList(); } } }
    public IReadOnlyList<FakeContainer> Containers { get { lock (_sync) { return _containers.Values.ToList(); } } }
    public IReadOnlyList<string> Volumes { get { lock (_sync) { return _volumes.Keys.ToList(); } } }
    public IReadOnlyList<string> Networks { get { lock (_sync) { return _networks.Keys.ToList(); } } }
    public IReadOnlySet<(string Network, string Container)> Attachments { get { lock (_sync) { return _attachments.ToHashSet(); } } }

    public FakeContainer? Find(string name)
    {
        lock (_sync)
        {
            return _containers.GetValueOrDefault(name);
        }
    }

    public void LoseActivationMarkers()
    {
        lock (_sync)
        {
            foreach (var marker in _containers.Values.Where(container => container.Role == BuilderLabels.ActivationRole).ToList())
            {
                _containers.Remove(marker.Name);
            }

            foreach (var marker in _volumes.Where(volume => volume.Value.TryGetValue(BuilderLabels.Role, out var role) && role == BuilderLabels.ActivationRole).ToList())
            {
                _volumes.Remove(marker.Key);
            }
        }
    }

    public IReadOnlyList<string> ActivationMarkers
    {
        get
        {
            lock (_sync)
            {
                return _containers.Values.Where(container => container.Role == BuilderLabels.ActivationRole).Select(container => container.Name)
                    .Concat(_volumes.Where(volume => volume.Value.TryGetValue(BuilderLabels.Role, out var role) && role == BuilderLabels.ActivationRole).Select(volume => volume.Key))
                    .ToList();
            }
        }
    }

    public void AddForeignContainer(string name, Guid owner)
    {
        lock (_sync)
        {
            _containers[name] = new FakeContainer(new CreateContainerParameters
            {
                Name = name,
                Labels = new Dictionary<string, string> { [BuilderLabels.Root] = owner.ToString() }
            })
            { Running = true };
        }
    }

    public Task<string> VersionAsync(CancellationToken ct) => Task.FromResult(EngineVersion);

    public Task<IReadOnlyList<BuilderContainer>> ListContainersAsync(Guid? server, CancellationToken ct)
    {
        lock (_sync)
        {
            IReadOnlyList<BuilderContainer> listed = _containers.Values
                .Where(container => server is null || container.Labels[BuilderLabels.Root] == server.Value.ToString())
                .Select(container => new BuilderContainer(container.Name, container.Labels.AsReadOnly(), container.Running))
                .ToList();
            return Task.FromResult(listed);
        }
    }

    public async Task CreateContainerAsync(Guid server, CreateContainerParameters parameters, CancellationToken ct)
    {
        ThrowIfInjected(BeforeCreate, parameters.Name);
        lock (_sync)
        {
            if (_containers.ContainsKey(parameters.Name))
            {
                throw new InvalidOperationException($"Name in use: {parameters.Name}");
            }

            _containers[parameters.Name] = new FakeContainer(parameters);
            _created.Add(parameters.Name);
        }

        await CompleteCreateAsync(parameters.Name, ct);
    }

    public Task StartContainerAsync(Guid server, string name, CancellationToken ct)
    {
        lock (_sync)
        {
            _containers[name].Running = true;
        }

        return Task.CompletedTask;
    }

    public Task StopContainerAsync(Guid server, string name, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_containers.TryGetValue(name, out var container))
            {
                container.Running = false;
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(Guid server, string name, CancellationToken ct)
    {
        ThrowIfInjected(BeforeRemove, name);
        lock (_sync)
        {
            _containers.Remove(name);
        }

        return Task.CompletedTask;
    }

    public async Task<long> WaitContainerAsync(Guid server, string name, CancellationToken ct)
    {
        if (BuildGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        lock (_sync)
        {
            var container = _containers[name];
            container.Running = false;
            if (BuildExitCode == 0 && container.MountSource("/env") is { } environment)
            {
                _completedEnvironments.Add(environment);
            }
        }

        return BuildExitCode;
    }

    public async Task ExtractArchiveAsync(Guid server, string name, string path, Stream archive, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await archive.CopyToAsync(buffer, ct);
        lock (_sync)
        {
            _extractions.Add(new Extraction(name, path, buffer.ToArray(), _containers[name].Running));
        }
    }

    public Task<string?> ReadFileAsync(Guid server, string name, string path, long maxBytes, CancellationToken ct)
    {
        lock (_sync)
        {
            var environment = _containers[name].MountSource("/env");
            var complete = path == "/env/requirements.lock" && environment is not null && _completedEnvironments.Contains(environment);
            if (complete && LockFile.Length > maxBytes)
            {
                throw new BuilderFileTooLargeException(path, maxBytes);
            }

            return Task.FromResult(complete ? LockFile : null);
        }
    }

    public async IAsyncEnumerable<string> ReadLogsAsync(Guid server, string name, int? tail, bool follow, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        string? role;
        lock (_sync)
        {
            role = _containers[name].Role;
        }

        yield return role == "build" ? BuildLog : ServerLog;
    }

    public async Task EnsureVolumeAsync(Guid server, VolumesCreateParameters parameters, CancellationToken ct)
    {
        ThrowIfInjected(BeforeCreate, parameters.Name);
        lock (_sync)
        {
            _volumes.TryAdd(parameters.Name, parameters.Labels);
        }

        await CompleteCreateAsync(parameters.Name, ct);
    }

    public Task<IReadOnlyList<BuilderVolume>> ListVolumesAsync(Guid server, CancellationToken ct)
    {
        lock (_sync)
        {
            IReadOnlyList<BuilderVolume> listed = _volumes
                .Where(volume => volume.Value[BuilderLabels.Root] == server.ToString())
                .Select(volume => new BuilderVolume(volume.Key, volume.Value.AsReadOnly()))
                .ToList();
            return Task.FromResult(listed);
        }
    }

    public Task RemoveVolumeAsync(Guid server, string name, CancellationToken ct)
    {
        ThrowIfInjected(BeforeRemove, name);
        lock (_sync)
        {
            _volumes.Remove(name);
            _completedEnvironments.Remove(name);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListSubnetsAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            IReadOnlyList<string> taken = _networks.Values
                .SelectMany(network => network.IPAM.Config.Select(config => config.Subnet))
                .Concat(Subnets)
                .ToList();
            return Task.FromResult(taken);
        }
    }

    public Task<IReadOnlyList<string>> ListNetworksAsync(Guid server, CancellationToken ct)
    {
        lock (_sync)
        {
            IReadOnlyList<string> listed = _networks
                .Where(network => network.Value.Labels[BuilderLabels.Root] == server.ToString())
                .Select(network => network.Key)
                .ToList();
            return Task.FromResult(listed);
        }
    }

    public Task<bool> NetworkExistsAsync(Guid server, string name, CancellationToken ct)
    {
        lock (_sync)
        {
            return Task.FromResult(_networks.ContainsKey(name));
        }
    }

    public Task CreateNetworkAsync(Guid server, NetworksCreateParameters parameters, CancellationToken ct)
    {
        lock (_sync)
        {
            _networks[parameters.Name] = parameters;
        }

        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(Guid server, string name, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_attachments.Any(attachment => attachment.Network == name))
            {
                throw new InvalidOperationException($"Network {name} has active endpoints.");
            }

            _networks.Remove(name);
            _removedNetworks.Add(name);
        }

        return Task.CompletedTask;
    }

    public Task ConnectAsync(Guid server, string network, string container, CancellationToken ct)
    {
        lock (_sync)
        {
            _attachments.Add((network, container));
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(Guid server, string network, string container, CancellationToken ct)
    {
        lock (_sync)
        {
            _attachments.Remove((network, container));
        }

        return Task.CompletedTask;
    }

    public Task<bool> RequireSystemContainerAsync(string name, CancellationToken ct) =>
        SystemContainers.Contains(name)
            ? Task.FromResult(true)
            : throw new BuilderObjectRefusedException($"Container {name} is not a builder system container.");

    private static void ThrowIfInjected(Func<string, Exception?>? injection, string name)
    {
        if (injection?.Invoke(name) is { } failure)
        {
            throw failure;
        }
    }

    private async Task CompleteCreateAsync(string name, CancellationToken ct)
    {
        ThrowIfInjected(AfterCreate, name);
        if (AfterCreateGate?.Invoke(name) is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }
    }
}
