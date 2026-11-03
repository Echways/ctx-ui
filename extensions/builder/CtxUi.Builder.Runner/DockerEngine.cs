using System.Formats.Tar;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace CtxUi.Builder.Runner;

public sealed class BuilderObjectRefusedException(string message) : Exception(message);

public sealed class BuilderFileTooLargeException(string path, long maxBytes)
    : Exception($"The file {path} is larger than the limit of {maxBytes / 1024} KB.");

public sealed record BuilderContainer(string Name, IReadOnlyDictionary<string, string> Labels, bool Running);

public sealed record BuilderVolume(string Name, IReadOnlyDictionary<string, string> Labels)
{
    public bool IsActivationMarker => Labels.GetValueOrDefault(BuilderLabels.Role) == BuilderLabels.ActivationRole;
}

public interface IDockerEngine
{
    Task<string> VersionAsync(CancellationToken ct);
    Task<IReadOnlyList<BuilderContainer>> ListContainersAsync(Guid? server, CancellationToken ct);
    Task CreateContainerAsync(Guid server, CreateContainerParameters parameters, CancellationToken ct);
    Task StartContainerAsync(Guid server, string name, CancellationToken ct);
    Task StopContainerAsync(Guid server, string name, CancellationToken ct);
    Task RemoveContainerAsync(Guid server, string name, CancellationToken ct);
    Task<long> WaitContainerAsync(Guid server, string name, CancellationToken ct);
    Task ExtractArchiveAsync(Guid server, string name, string path, Stream archive, CancellationToken ct);
    Task<string?> ReadFileAsync(Guid server, string name, string path, long maxBytes, CancellationToken ct);
    IAsyncEnumerable<string> ReadLogsAsync(Guid server, string name, int? tail, bool follow, CancellationToken ct);
    Task EnsureVolumeAsync(Guid server, VolumesCreateParameters parameters, CancellationToken ct);
    Task<IReadOnlyList<BuilderVolume>> ListVolumesAsync(Guid server, CancellationToken ct);
    Task RemoveVolumeAsync(Guid server, string name, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSubnetsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListNetworksAsync(Guid server, CancellationToken ct);
    Task<bool> NetworkExistsAsync(Guid server, string name, CancellationToken ct);
    Task CreateNetworkAsync(Guid server, NetworksCreateParameters parameters, CancellationToken ct);
    Task RemoveNetworkAsync(Guid server, string name, CancellationToken ct);
    Task ConnectAsync(Guid server, string network, string container, CancellationToken ct);
    Task DisconnectAsync(Guid server, string network, string container, CancellationToken ct);
    Task<bool> RequireSystemContainerAsync(string name, CancellationToken ct);
}

public sealed class DockerEngine : IDockerEngine, IDisposable
{
    private const uint StopGraceSeconds = 10;
    private const int LogBufferBytes = 8192;
    private const int ArchiveReadBufferBytes = 81920;
    private const int ArchiveOverheadBytes = 16 * 1024;
    private const int NoDefaultRoutePriority = -1000;

    private readonly DockerClient _client;

    public DockerEngine(Uri endpoint) => _client = new DockerClientConfiguration(endpoint).CreateClient();

    public void Dispose() => _client.Dispose();

    public async Task<string> VersionAsync(CancellationToken ct) => (await _client.System.GetVersionAsync(ct)).Version;

    public async Task<IReadOnlyList<BuilderContainer>> ListContainersAsync(Guid? server, CancellationToken ct)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter(server)
        }, ct);

        return containers
            .Where(container => container.Labels.TryGetValue(BuilderLabels.Root, out var owner) && Guid.TryParse(owner, out _))
            .Select(container => new BuilderContainer(
                container.Names.First().TrimStart('/'),
                container.Labels.AsReadOnly(),
                container.State is "running" or "restarting" or "paused"))
            .ToList();
    }

    public async Task CreateContainerAsync(Guid server, CreateContainerParameters parameters, CancellationToken ct)
    {
        RequireOwnLabel(server, parameters.Labels, $"container {parameters.Name}");
        await _client.Containers.CreateContainerAsync(parameters, ct);
    }

    public async Task StartContainerAsync(Guid server, string name, CancellationToken ct)
    {
        await RequireContainerAsync(server, name, ct);
        await _client.Containers.StartContainerAsync(name, new ContainerStartParameters(), ct);
    }

    public async Task StopContainerAsync(Guid server, string name, CancellationToken ct)
    {
        await RequireContainerAsync(server, name, ct);
        await _client.Containers.StopContainerAsync(name, new ContainerStopParameters { WaitBeforeKillSeconds = StopGraceSeconds }, ct);
    }

    public async Task RemoveContainerAsync(Guid server, string name, CancellationToken ct)
    {
        if (await FindContainerAsync(name, ct) is not { } container)
        {
            return;
        }

        RequireOwnLabel(server, container.Config.Labels, $"container {name}");
        await IgnoreMissing(() => _client.Containers.RemoveContainerAsync(name, new ContainerRemoveParameters { Force = true }, ct));
    }

    public async Task<long> WaitContainerAsync(Guid server, string name, CancellationToken ct)
    {
        await RequireContainerAsync(server, name, ct);
        return (await _client.Containers.WaitContainerAsync(name, ct)).StatusCode;
    }

    public async Task ExtractArchiveAsync(Guid server, string name, string path, Stream archive, CancellationToken ct)
    {
        await RequireContainerAsync(server, name, ct);
        await _client.Containers.ExtractArchiveToContainerAsync(name, new ContainerPathStatParameters { Path = path }, archive, ct);
    }

    public async Task<string?> ReadFileAsync(Guid server, string name, string path, long maxBytes, CancellationToken ct)
    {
        await RequireContainerAsync(server, name, ct);
        try
        {
            var response = await _client.Containers.GetArchiveFromContainerAsync(name, new GetArchiveFromContainerParameters { Path = path }, false, ct);
            await using var archive = response.Stream;
            if (response.Stat?.Size > maxBytes)
            {
                throw new BuilderFileTooLargeException(path, maxBytes);
            }

            using var buffered = await BufferBoundedAsync(archive, path, maxBytes, ct);
            await using var tar = new TarReader(buffered);
            var entry = await tar.GetNextEntryAsync(copyData: false, ct);
            if (entry?.Length > maxBytes)
            {
                throw new BuilderFileTooLargeException(path, maxBytes);
            }

            if (entry?.DataStream is not { } data)
            {
                return null;
            }

            using var reader = new StreamReader(data, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
        catch (DockerApiException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<MemoryStream> BufferBoundedAsync(Stream archive, string path, long maxBytes, CancellationToken ct)
    {
        var buffered = new MemoryStream();
        var chunk = new byte[ArchiveReadBufferBytes];
        int read;
        while ((read = await archive.ReadAsync(chunk, ct)) > 0)
        {
            if (buffered.Length + read > maxBytes + ArchiveOverheadBytes)
            {
                await buffered.DisposeAsync();
                throw new BuilderFileTooLargeException(path, maxBytes);
            }

            buffered.Write(chunk, 0, read);
        }

        buffered.Position = 0;
        return buffered;
    }

    public async IAsyncEnumerable<string> ReadLogsAsync(Guid server, string name, int? tail, bool follow, [EnumeratorCancellation] CancellationToken ct)
    {
        await RequireContainerAsync(server, name, ct);
        using var stream = await _client.Containers.GetContainerLogsAsync(name, tty: false, new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = follow,
            Tail = tail?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"
        }, ct);

        var buffer = new byte[LogBufferBytes];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[Encoding.UTF8.GetMaxCharCount(LogBufferBytes)];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF)
            {
                yield break;
            }

            var count = decoder.GetChars(buffer, 0, read.Count, characters, 0);
            if (count > 0)
            {
                yield return new string(characters, 0, count);
            }
        }
    }

    public async Task EnsureVolumeAsync(Guid server, VolumesCreateParameters parameters, CancellationToken ct)
    {
        RequireOwnLabel(server, parameters.Labels, $"volume {parameters.Name}");
        if (await FindVolumeAsync(parameters.Name, ct) is { } existing)
        {
            RequireOwnLabel(server, existing.Labels, $"volume {parameters.Name}");
            return;
        }

        await _client.Volumes.CreateAsync(parameters, ct);
    }

    public async Task<IReadOnlyList<BuilderVolume>> ListVolumesAsync(Guid server, CancellationToken ct)
    {
        var volumes = await _client.Volumes.ListAsync(new VolumesListParameters { Filters = LabelFilter(server) }, ct);
        return (volumes.Volumes ?? [])
            .Select(volume => new BuilderVolume(volume.Name, (volume.Labels ?? new Dictionary<string, string>()).AsReadOnly()))
            .ToList();
    }

    public async Task RemoveVolumeAsync(Guid server, string name, CancellationToken ct)
    {
        if (await FindVolumeAsync(name, ct) is not { } volume)
        {
            return;
        }

        RequireOwnLabel(server, volume.Labels, $"volume {name}");
        await IgnoreMissing(() => _client.Volumes.RemoveAsync(name, false, ct));
    }

    public async Task<IReadOnlyList<string>> ListSubnetsAsync(CancellationToken ct)
    {
        var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters(), ct);
        return networks
            .SelectMany(network => network.IPAM?.Config ?? [])
            .Select(config => config.Subnet)
            .Where(subnet => !string.IsNullOrEmpty(subnet))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListNetworksAsync(Guid server, CancellationToken ct)
    {
        var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters { Filters = LabelFilter(server) }, ct);
        return networks.Select(network => network.Name).ToList();
    }

    public async Task<bool> NetworkExistsAsync(Guid server, string name, CancellationToken ct)
    {
        if (await FindNetworkAsync(name, ct) is not { } network)
        {
            return false;
        }

        RequireOwnLabel(server, network.Labels, $"network {name}");
        return true;
    }

    public async Task CreateNetworkAsync(Guid server, NetworksCreateParameters parameters, CancellationToken ct)
    {
        RequireOwnLabel(server, parameters.Labels, $"network {parameters.Name}");
        await _client.Networks.CreateNetworkAsync(parameters, ct);
    }

    public async Task RemoveNetworkAsync(Guid server, string name, CancellationToken ct)
    {
        if (await FindNetworkAsync(name, ct) is not { } network)
        {
            return;
        }

        RequireOwnLabel(server, network.Labels, $"network {name}");
        await IgnoreMissing(() => _client.Networks.DeleteNetworkAsync(name, ct));
    }

    public async Task ConnectAsync(Guid server, string network, string container, CancellationToken ct)
    {
        var inspected = await RequireConnectableAsync(server, network, container, ct);
        if (inspected.NetworkSettings?.Networks?.ContainsKey(network) == true)
        {
            return;
        }

        await _client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters
        {
            Container = container,
            EndpointConfig = new PrioritizedEndpointSettings { GwPriority = NoDefaultRoutePriority }
        }, ct);
    }

    public async Task DisconnectAsync(Guid server, string network, string container, CancellationToken ct)
    {
        var inspected = await RequireConnectableAsync(server, network, container, ct);
        if (inspected.NetworkSettings?.Networks?.ContainsKey(network) != true)
        {
            return;
        }

        await _client.Networks.DisconnectNetworkAsync(network, new NetworkDisconnectParameters { Container = container, Force = true }, ct);
    }

    public async Task<bool> RequireSystemContainerAsync(string name, CancellationToken ct)
    {
        var container = await FindContainerAsync(name, ct)
            ?? throw new BuilderObjectRefusedException($"Container {name} does not exist.");
        if (Owner(container.Config.Labels) != BuilderLabels.SystemValue)
        {
            throw new BuilderObjectRefusedException($"Container {name} does not carry the label {BuilderLabels.Root}={BuilderLabels.SystemValue}.");
        }

        return container.State?.Running == true;
    }

    private async Task<ContainerInspectResponse> RequireConnectableAsync(Guid server, string network, string container, CancellationToken ct)
    {
        var inspected = await _client.Containers.InspectContainerAsync(container, ct);
        var owner = Owner(inspected.Config.Labels);
        if (owner != server.ToString() && owner != BuilderLabels.SystemValue)
        {
            throw new BuilderObjectRefusedException($"Container {container} does not belong to server {server}.");
        }

        var attached = await _client.Networks.InspectNetworkAsync(network, ct);
        RequireOwnLabel(server, attached.Labels, $"network {network}");
        return inspected;
    }

    private async Task RequireContainerAsync(Guid server, string name, CancellationToken ct)
    {
        var container = await _client.Containers.InspectContainerAsync(name, ct);
        RequireOwnLabel(server, container.Config.Labels, $"container {name}");
    }

    private async Task<ContainerInspectResponse?> FindContainerAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _client.Containers.InspectContainerAsync(name, ct);
        }
        catch (DockerApiException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<VolumeResponse?> FindVolumeAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _client.Volumes.InspectAsync(name, ct);
        }
        catch (DockerApiException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<NetworkResponse?> FindNetworkAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _client.Networks.InspectNetworkAsync(name, ct);
        }
        catch (DockerApiException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task IgnoreMissing(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static string? Owner(IDictionary<string, string>? labels) =>
        labels is not null && labels.TryGetValue(BuilderLabels.Root, out var owner) ? owner : null;

    private static void RequireOwnLabel(Guid server, IDictionary<string, string>? labels, string description)
    {
        if (Owner(labels) != server.ToString())
        {
            throw new BuilderObjectRefusedException($"The {description} does not belong to server {server}.");
        }
    }

    private static Dictionary<string, IDictionary<string, bool>> LabelFilter(Guid? server) => new()
    {
        ["label"] = new Dictionary<string, bool>
        {
            [server is null ? BuilderLabels.Root : $"{BuilderLabels.Root}={server}"] = true
        }
    };

    [DataContract]
    private sealed class PrioritizedEndpointSettings : EndpointSettings
    {
        [DataMember(Name = "GwPriority", EmitDefaultValue = false)]
        public int GwPriority { get; init; }
    }
}
