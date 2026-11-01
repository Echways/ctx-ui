using System.Globalization;
using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Runner;

public sealed class Reconciler(
    IDockerEngine engine,
    ServerRegistry registry,
    ServerNetworks networks,
    RouteTable routes,
    ReleaseRunner runner,
    RunnerOptions options,
    ILogger<Reconciler> logger) : IHostedService
{
    private const int MinimumDockerMajorVersion = 29;

    public static bool IsSupportedDockerVersion(string version)
    {
        var major = version.Split('.')[0];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= MinimumDockerMajorVersion;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var version = await engine.VersionAsync(cancellationToken);
        if (!IsSupportedDockerVersion(version))
        {
            throw new InvalidOperationException($"Docker Engine {MinimumDockerMajorVersion} or newer is required, this host runs {version}.");
        }

        await engine.RequireSystemContainerAsync(options.SelfContainer, cancellationToken);
        await engine.RequireSystemContainerAsync(options.EgressContainer, cancellationToken);

        var containers = await engine.ListContainersAsync(null, cancellationToken);
        foreach (var group in containers.GroupBy(container => Guid.Parse(container.Labels[BuilderLabels.Root])))
        {
            try
            {
                await RestoreAsync(group.Key, group.ToList(), cancellationToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                logger.LogError("Restoring server {Server} failed: {Reason}", group.Key, failure.Message);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RestoreAsync(Guid id, IReadOnlyList<BuilderContainer> containers, CancellationToken ct)
    {
        var server = registry.GetOrAdd(id);
        await RemoveInterruptedBuildsAsync(server, containers, ct);

        var servers = containers.Where(container => !container.Labels.ContainsKey(BuilderLabels.Role)).ToList();
        var marker = await CurrentMarkerAsync(id, servers, ct);
        if (marker is null)
        {
            ProtectUnmarked(server, servers);
        }

        var active = marker?.Labels[BuilderLabels.ActiveContainer];
        var retiring = (marker?.Labels.GetValueOrDefault(BuilderLabels.RetiringContainers) ?? "")
            .Split(BuilderLabels.ListSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var container in servers.OrderBy(container => StateRank(container, active, retiring)))
        {
            await RestoreServerContainerAsync(server, container, active, retiring, ct);
        }
    }

    private static int StateRank(BuilderContainer container, string? active, HashSet<string> retiring)
    {
        if (container.Name == active)
        {
            return 3;
        }

        if (!container.Running)
        {
            return 0;
        }

        return retiring.Contains(container.Name) ? 1 : 2;
    }

    private async Task RemoveInterruptedBuildsAsync(ServerRecord server, IReadOnlyList<BuilderContainer> containers, CancellationToken ct)
    {
        var interrupted = containers.Where(container =>
            container.Labels.GetValueOrDefault(BuilderLabels.Role) is BuilderLabels.BuildRole or BuilderLabels.UnpackRole);
        foreach (var container in interrupted)
        {
            var number = int.Parse(container.Labels[BuilderLabels.Release], CultureInfo.InvariantCulture);
            await engine.RemoveContainerAsync(server.Id, container.Name, ct);
            await RemoveVolumesOfInterruptedBuildAsync(server.Id, container, number, ct);

            var release = new ReleaseRecord(number, ReleaseState.Failed);
            release.SetEnvironmentHash(container.Labels[BuilderLabels.EnvironmentHash]);
            release.MarkFailed("The build was interrupted by a restart of Runner. Publish again.");
            lock (server.Sync)
            {
                server.Releases[number] = release;
            }

            logger.LogWarning("Removed the interrupted build {Container}", container.Name);
        }
    }

    private async Task RemoveVolumesOfInterruptedBuildAsync(Guid id, BuilderContainer container, int number, CancellationToken ct)
    {
        try
        {
            await engine.RemoveVolumeAsync(id, ContainerTemplate.CodeVolume(id, number), ct);
            if (container.Labels.GetValueOrDefault(BuilderLabels.Role) == BuilderLabels.BuildRole)
            {
                await engine.RemoveVolumeAsync(id, ContainerTemplate.EnvironmentVolume(id, container.Labels[BuilderLabels.EnvironmentHash]), ct);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogWarning("Removing the volumes of the interrupted build {Container} failed: {Reason}", container.Name, failure.Message);
        }
    }

    private void ProtectUnmarked(ServerRecord server, IReadOnlyList<BuilderContainer> servers)
    {
        var running = servers.Where(container => container.Running).Select(container => container.Name).ToList();
        if (running.Count == 0)
        {
            return;
        }

        lock (server.Sync)
        {
            server.Unmarked.UnionWith(running);
        }

        logger.LogError(
            "Server {Server} has {Count} running containers and no activation marker, so its active route cannot be restored. They are kept running until a release is activated.",
            server.Id,
            running.Count);
    }

    private async Task<BuilderVolume?> CurrentMarkerAsync(Guid id, IReadOnlyList<BuilderContainer> servers, CancellationToken ct)
    {
        var markers = (await engine.ListVolumesAsync(id, ct))
            .Where(volume => volume.IsActivationMarker)
            .OrderByDescending(marker => int.Parse(marker.Labels[BuilderLabels.Activation], CultureInfo.InvariantCulture))
            .ToList();
        var current = markers.FirstOrDefault(marker => servers.Any(container => container.Name == marker.Labels[BuilderLabels.ActiveContainer]));
        foreach (var stale in markers.Where(marker => marker != current))
        {
            try
            {
                await engine.RemoveVolumeAsync(id, stale.Name, ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                logger.LogWarning("Removing the stale activation marker {Marker} failed: {Reason}", stale.Name, failure.Message);
            }
        }

        return current;
    }

    private async Task RestoreServerContainerAsync(ServerRecord server, BuilderContainer container, string? active, HashSet<string> retiring, CancellationToken ct)
    {
        var id = server.Id;
        var number = int.Parse(container.Labels[BuilderLabels.Release], CultureInfo.InvariantCulture);
        var isActive = container.Name == active;
        var isRetiring = retiring.Contains(container.Name) && container.Running;

        if ((container.Running || isActive) && Enum.TryParse<NetworkAccess>(container.Labels.GetValueOrDefault(BuilderLabels.Network), out var access))
        {
            await networks.AttachAsync(id, access, ct);
        }

        var secretHash = container.Labels[BuilderLabels.RouteSecretHash];
        ReleaseRecord release;
        lock (server.Sync)
        {
            if (!server.Releases.TryGetValue(number, out var known))
            {
                known = new ReleaseRecord(number, ReleaseState.Retired);
                server.Releases[number] = known;
            }

            release = known;
            release.SetEnvironmentHash(container.Labels[BuilderLabels.EnvironmentHash]);
        }

        if (isActive)
        {
            routes.SetActive(id, container.Name, secretHash);
            lock (server.Sync)
            {
                server.ActiveRelease = number;
                server.ActiveContainer = container.Name;
                if (!server.Candidates.ContainsKey(number))
                {
                    release.MarkRunnable(ReleaseState.Active);
                }
            }
        }
        else if (isRetiring)
        {
            lock (server.Sync)
            {
                server.Retiring.Add(container.Name);
                release.MarkRunnable(ReleaseState.Retired);
            }

            runner.ScheduleRetirement(server, container.Name);
        }
        else if (container.Running)
        {
            routes.SetCandidate(id, number, container.Name, secretHash);
            lock (server.Sync)
            {
                server.Candidates[number] = container.Name;
                release.MarkRunnable(ReleaseState.Candidate);
            }
        }
        else
        {
            release.MarkRunnable(ReleaseState.Retired);
        }
    }
}
