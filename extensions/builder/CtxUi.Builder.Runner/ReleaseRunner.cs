using System.Globalization;
using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Runner;

public sealed class ReleaseRunner(
    IDockerEngine engine,
    ServerRegistry registry,
    ServerNetworks networks,
    RouteTable routes,
    IServerProbe probe,
    RunnerOptions options,
    TimeProvider time,
    ILogger<ReleaseRunner> logger)
{
    private const int KeptReleases = 5;
    private const int FailureLogLines = 200;

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(30);

    public void BeginCandidate(Guid id, int number, IReadOnlyDictionary<string, string> environment)
    {
        var server = registry.Require(id);
        ServerSpec spec;
        ReleaseRecord release;
        string hash;
        lock (server.Sync)
        {
            spec = server.Spec ?? throw RunnerRequestException.Conflict($"The specification of server {id} is not known. Send it with PUT before a start.");
            release = server.RequireRelease(number);
            if (!release.Runnable || release.EnvironmentHash is null)
            {
                throw RunnerRequestException.Conflict($"Release {number} has not been built.");
            }

            if (server.StartingRelease is { } starting)
            {
                throw RunnerRequestException.Conflict($"Release {starting} of this server is still starting.");
            }

            hash = release.EnvironmentHash;
            server.StartingRelease = number;
            release.MarkRunnable(ReleaseState.Built);
        }

        server.Track(RunCandidateAsync(server, release, hash, spec, environment));
    }

    public async Task<ReleaseStatus> ActivateAsync(Guid id, int number, CancellationToken ct)
    {
        var server = registry.Require(id);
        var release = server.RequireRelease(number);
        await server.Operations.WaitAsync(ct);
        try
        {
            string candidate;
            string? previous;
            int? previousRelease;
            lock (server.Sync)
            {
                if (!server.Candidates.TryGetValue(number, out var known))
                {
                    if (server.ActiveRelease == number)
                    {
                        return release.ToStatus();
                    }

                    throw RunnerRequestException.Conflict($"Release {number} is not a healthy candidate.");
                }

                candidate = known;
                previous = server.ActiveContainer;
                previousRelease = server.ActiveRelease;
            }

            if (!await probe.IsHealthyAsync(candidate, ct))
            {
                throw RunnerRequestException.Conflict($"Release {number} did not answer its health check. The route stays where it is.");
            }

            var containers = await engine.ListContainersAsync(id, ct);
            var secretHash = containers.FirstOrDefault(container => container.Name == candidate)?.Labels[BuilderLabels.RouteSecretHash]
                ?? throw RunnerRequestException.Conflict($"The container of release {number} no longer exists.");
            await RecordActivationAsync(server, containers, number, candidate, previous, ct);

            routes.ActivateCandidate(id, number, candidate, secretHash);
            lock (server.Sync)
            {
                server.Candidates.Remove(number);
                server.ActiveRelease = number;
                server.ActiveContainer = candidate;
                release.MarkState(ReleaseState.Active);
                if (previousRelease is { } old && old != number && server.Releases.TryGetValue(old, out var retired))
                {
                    retired.MarkState(ReleaseState.Retired);
                }

                if (previous is not null)
                {
                    server.Retiring.Add(previous);
                }
            }

            if (previous is not null)
            {
                ScheduleRetirement(server, previous);
            }

            return release.ToStatus();
        }
        finally
        {
            server.Operations.Release();
        }
    }

    public void ScheduleRetirement(ServerRecord server, string container) =>
        server.Track(RetireAfterDrainAsync(server, container));

    public async Task StopAsync(Guid id, CancellationToken ct)
    {
        var server = registry.Require(id);
        await server.Operations.WaitAsync(ct);
        try
        {
            var active = RequireActive(server);
            await engine.StopContainerAsync(id, active, ct);
        }
        finally
        {
            server.Operations.Release();
        }
    }

    public async Task StartAsync(Guid id, CancellationToken ct)
    {
        var server = registry.Require(id);
        await server.Operations.WaitAsync(ct);
        try
        {
            var active = RequireActive(server);
            await engine.StartContainerAsync(id, active, ct);
        }
        finally
        {
            server.Operations.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (registry.Remove(id) is { } server)
        {
            await server.Lifetime.CancelAsync();
            await server.WorkFinishedAsync();
        }

        routes.RemoveServer(id);
        foreach (var container in await engine.ListContainersAsync(id, ct))
        {
            await engine.RemoveContainerAsync(id, container.Name, ct);
        }

        foreach (var network in await engine.ListNetworksAsync(id, ct))
        {
            await networks.RemoveAsync(id, network, ct);
        }

        foreach (var volume in await engine.ListVolumesAsync(id, ct))
        {
            await engine.RemoveVolumeAsync(id, volume, ct);
        }
    }

    public async Task<string> RequireLogContainerAsync(Guid id, CancellationToken ct)
    {
        if (registry.Find(id) is { } server)
        {
            lock (server.Sync)
            {
                if (server.ActiveContainer is { } active)
                {
                    return active;
                }

                if (server.Candidates.Count > 0)
                {
                    return server.Candidates[server.Candidates.Keys.Max()];
                }
            }
        }

        var containers = await engine.ListContainersAsync(id, ct);
        return containers
            .Where(container => !container.Labels.ContainsKey(BuilderLabels.Role))
            .OrderByDescending(container => container.Running)
            .Select(container => container.Name)
            .FirstOrDefault()
            ?? throw RunnerRequestException.NotFound($"Server {id} has no container to read logs from.");
    }

    private static string RequireActive(ServerRecord server)
    {
        lock (server.Sync)
        {
            return server.ActiveContainer ?? throw RunnerRequestException.Conflict($"Server {server.Id} has no active release.");
        }
    }

    private async Task RecordActivationAsync(ServerRecord server, IReadOnlyList<BuilderContainer> containers, int number, string candidate, string? previous, CancellationToken ct)
    {
        var markers = containers.Where(container => container.Labels.GetValueOrDefault(BuilderLabels.Role) == BuilderLabels.ActivationRole).ToList();
        var sequence = markers.Count == 0
            ? 1
            : markers.Max(marker => int.Parse(marker.Labels[BuilderLabels.Activation], CultureInfo.InvariantCulture)) + 1;

        List<string> retiring;
        lock (server.Sync)
        {
            retiring = server.Retiring.ToList();
        }

        if (previous is not null)
        {
            retiring.Add(previous);
        }

        await engine.CreateContainerAsync(server.Id, ContainerTemplate.ActivationMarker(server.Id, sequence, number, candidate, retiring, options), ct);
        foreach (var marker in markers)
        {
            await engine.RemoveContainerAsync(server.Id, marker.Name, ct);
        }
    }

    private async Task RunCandidateAsync(ServerRecord server, ReleaseRecord release, string hash, ServerSpec spec, IReadOnlyDictionary<string, string> environment)
    {
        await Task.Yield();
        var id = server.Id;
        var ct = server.Lifetime.Token;
        string? created = null;
        try
        {
            await networks.EnsureAsync(id, spec.Network, ct);
            await engine.EnsureVolumeAsync(id, ContainerTemplate.Volume(id, ContainerTemplate.DataVolume(id)), ct);
            created = await CreateCandidateAsync(server, release.Number, hash, spec, environment, ct);
            await WaitUntilHealthyAsync(created, ct);

            var secretHash = ContainerTemplate.RouteSecretHash(spec.RouteSecret);
            await server.Operations.WaitAsync(ct);
            try
            {
                routes.SetCandidate(id, release.Number, created, secretHash);
                lock (server.Sync)
                {
                    server.Candidates[release.Number] = created;
                    server.StartingRelease = null;
                    release.MarkRunnable(ReleaseState.Candidate);
                }
            }
            finally
            {
                server.Operations.Release();
            }
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Start of release {Release} of server {Server} failed", release.Number, id);
            if (!ct.IsCancellationRequested && created is not null)
            {
                await RemoveFailedCandidateAsync(id, release, created);
            }

            lock (server.Sync)
            {
                server.StartingRelease = null;
                release.MarkFailed(failure is OperationCanceledException ? "The start was cancelled." : failure.Message);
            }
        }
    }

    private async Task<string> CreateCandidateAsync(ServerRecord server, int number, string hash, ServerSpec spec, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var id = server.Id;
        await server.Operations.WaitAsync(ct);
        try
        {
            var parameters = ContainerTemplate.Server(id, number, hash, spec, environment, options);
            var containers = await engine.ListContainersAsync(id, ct);
            var kept = new HashSet<string>(StringComparer.Ordinal);
            await RemoveOtherCandidatesAsync(server, ct);
            lock (server.Sync)
            {
                if (server.ActiveContainer is { } active)
                {
                    kept.Add(active);
                }

                kept.UnionWith(server.Retiring);
            }

            foreach (var container in containers.Where(container => IsServerContainerOf(container, number) && !kept.Contains(container.Name)))
            {
                await engine.RemoveContainerAsync(id, container.Name, ct);
            }

            if (kept.Contains(parameters.Name))
            {
                parameters.Name = $"{parameters.Name}-{Guid.NewGuid():N}"[..(parameters.Name.Length + 9)];
            }

            await engine.CreateContainerAsync(id, parameters, ct);
            await engine.StartContainerAsync(id, parameters.Name, ct);
            return parameters.Name;
        }
        finally
        {
            server.Operations.Release();
        }
    }

    private async Task RemoveOtherCandidatesAsync(ServerRecord server, CancellationToken ct)
    {
        List<KeyValuePair<int, string>> candidates;
        lock (server.Sync)
        {
            candidates = server.Candidates.ToList();
            server.Candidates.Clear();
        }

        foreach (var (number, container) in candidates)
        {
            routes.RemoveCandidate(server.Id, number);
            await engine.RemoveContainerAsync(server.Id, container, ct);
            lock (server.Sync)
            {
                if (server.ActiveRelease != number && server.Releases.TryGetValue(number, out var release))
                {
                    release.MarkRunnable(ReleaseState.Built);
                }
            }
        }
    }

    private static bool IsServerContainerOf(BuilderContainer container, int number) =>
        !container.Labels.ContainsKey(BuilderLabels.Role)
        && container.Labels.GetValueOrDefault(BuilderLabels.Release) == number.ToString(CultureInfo.InvariantCulture);

    private async Task WaitUntilHealthyAsync(string container, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(HealthTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            while (!await probe.IsHealthyAsync(container, linked.Token))
            {
                await Task.Delay(HealthInterval, time, linked.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The server did not answer /healthz within {HealthTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task RemoveFailedCandidateAsync(Guid id, ReleaseRecord release, string container)
    {
        try
        {
            await foreach (var chunk in engine.ReadLogsAsync(id, container, FailureLogLines, false, CancellationToken.None))
            {
                release.AppendLog(chunk);
            }
        }
        catch (Exception failure)
        {
            logger.LogWarning("Reading the log of {Container} failed: {Reason}", container, failure.Message);
        }

        try
        {
            await engine.RemoveContainerAsync(id, container, CancellationToken.None);
        }
        catch (Exception failure)
        {
            logger.LogWarning("Removing the failed candidate {Container} failed: {Reason}", container, failure.Message);
        }
    }

    private async Task RetireAfterDrainAsync(ServerRecord server, string container)
    {
        var ct = server.Lifetime.Token;
        try
        {
            await Task.Delay(DrainTime, time, ct);
            await server.Operations.WaitAsync(ct);
            try
            {
                await RetireAsync(server, container, ct);
                await RemoveUnusedNetworksAsync(server, ct);
                await PruneAsync(server, ct);
            }
            finally
            {
                server.Operations.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception failure)
        {
            logger.LogWarning("Retiring {Container} failed: {Reason}", container, failure.Message);
        }
    }

    private async Task RetireAsync(ServerRecord server, string container, CancellationToken ct)
    {
        var containers = await engine.ListContainersAsync(server.Id, ct);
        var retiring = containers.FirstOrDefault(candidate => candidate.Name == container);
        lock (server.Sync)
        {
            server.Retiring.Remove(container);
        }

        if (retiring is null)
        {
            return;
        }

        var release = retiring.Labels[BuilderLabels.Release];
        var replaced = containers.Any(other =>
            other.Name != container
            && !other.Labels.ContainsKey(BuilderLabels.Role)
            && other.Labels.GetValueOrDefault(BuilderLabels.Release) == release);
        if (replaced)
        {
            await engine.RemoveContainerAsync(server.Id, container, ct);
        }
        else
        {
            await engine.StopContainerAsync(server.Id, container, ct);
        }
    }

    private async Task RemoveUnusedNetworksAsync(ServerRecord server, CancellationToken ct)
    {
        var containers = await engine.ListContainersAsync(server.Id, ct);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string? active;
        lock (server.Sync)
        {
            if (server.BuildingRelease is not null || server.StartingRelease is not null)
            {
                return;
            }

            active = server.ActiveContainer;
            if (server.Spec is { } spec)
            {
                used.Add(spec.Network.ToString());
            }
        }

        foreach (var container in containers.Where(container => !container.Labels.ContainsKey(BuilderLabels.Role) && (container.Running || container.Name == active)))
        {
            if (container.Labels.TryGetValue(BuilderLabels.Network, out var network))
            {
                used.Add(network);
            }
        }

        var existing = await engine.ListNetworksAsync(server.Id, ct);
        foreach (var access in Enum.GetValues<NetworkAccess>().Where(access => !used.Contains(access.ToString())))
        {
            var name = ContainerTemplate.NetworkName(server.Id, access);
            if (existing.Contains(name))
            {
                await networks.RemoveAsync(server.Id, name, ct);
            }
        }
    }

    private async Task PruneAsync(ServerRecord server, CancellationToken ct)
    {
        var id = server.Id;
        var codePrefix = $"{ContainerTemplate.Prefix(id)}-code-";
        var environmentPrefix = $"{ContainerTemplate.Prefix(id)}-env-";
        var volumes = await engine.ListVolumesAsync(id, ct);
        var containers = (await engine.ListContainersAsync(id, ct))
            .Where(container => !container.Labels.ContainsKey(BuilderLabels.Role))
            .ToList();

        var numbers = volumes
            .Where(volume => volume.StartsWith(codePrefix, StringComparison.Ordinal))
            .Select(volume => int.TryParse(volume[codePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : (int?)null)
            .OfType<int>()
            .ToList();

        var kept = numbers.OrderDescending().Take(KeptReleases).ToHashSet();
        lock (server.Sync)
        {
            kept.UnionWith(server.Candidates.Keys);
            if (server.ActiveRelease is { } active)
            {
                kept.Add(active);
            }

            if (server.BuildingRelease is { } building)
            {
                kept.Add(building);
            }

            if (server.StartingRelease is { } starting)
            {
                kept.Add(starting);
            }
        }

        foreach (var container in containers.Where(container => container.Running))
        {
            kept.Add(int.Parse(container.Labels[BuilderLabels.Release], CultureInfo.InvariantCulture));
        }

        foreach (var number in numbers.Where(number => !kept.Contains(number)))
        {
            foreach (var container in containers.Where(container => IsServerContainerOf(container, number)))
            {
                await engine.RemoveContainerAsync(id, container.Name, ct);
            }

            await engine.RemoveVolumeAsync(id, ContainerTemplate.CodeVolume(id, number), ct);
            lock (server.Sync)
            {
                server.Releases.Remove(number);
            }
        }

        var keptEnvironments = new HashSet<string>(StringComparer.Ordinal);
        lock (server.Sync)
        {
            foreach (var release in server.Releases.Values.Where(release => kept.Contains(release.Number) && release.EnvironmentHash is not null))
            {
                keptEnvironments.Add(ContainerTemplate.EnvironmentVolume(id, release.EnvironmentHash!));
            }
        }

        foreach (var volume in volumes.Where(volume => volume.StartsWith(environmentPrefix, StringComparison.Ordinal) && !keptEnvironments.Contains(volume)))
        {
            await engine.RemoveVolumeAsync(id, volume, ct);
        }
    }
}
