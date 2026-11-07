using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Runner;

public sealed class ReleaseBuilder(
    IDockerEngine engine,
    ServerRegistry registry,
    ServerNetworks networks,
    RunnerOptions options,
    TimeProvider time,
    ILogger<ReleaseBuilder> logger)
{
    private const string LockFilePath = "/env/requirements.lock";
    private const string CodePath = "/code";
    private const long MaxLockFileBytes = 1024 * 1024;

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    public void Begin(Guid id, int number, byte[] archive)
    {
        var server = registry.Require(id);
        ServerSpec spec;
        ReleaseRecord release;
        lock (server.Sync)
        {
            spec = server.Spec ?? throw RunnerRequestException.SpecMissing(id);
            if (server.BuildingRelease is { } running)
                throw RunnerRequestException.Conflict(
                    $"Release {running} of this server is still building. Only one build runs at a time.");

            if (server.Releases.TryGetValue(number, out var existing) &&
                (existing.State != ReleaseState.Failed || existing.Runnable))
                throw RunnerRequestException.Conflict($"Release {number} already exists with state {existing.State}.");

            release = new ReleaseRecord(number, ReleaseState.Building);
            server.Releases[number] = release;
            server.BuildingRelease = number;
        }

        server.Track(RunAsync(server, release, spec, archive));
    }

    private async Task RunAsync(ServerRecord server, ReleaseRecord release, ServerSpec spec, byte[] archive)
    {
        await Task.Yield();
        var attempt = new BuildAttempt();
        var ct = server.Lifetime.Token;
        try
        {
            var lockFile = await BuildAsync(server.Id, release, spec, archive, attempt, ct);
            lock (server.Sync)
            {
                server.BuildingRelease = null;
                release.MarkBuilt(lockFile);
            }
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Build of release {Release} of server {Server} failed", release.Number,
                server.Id);
            if (!ct.IsCancellationRequested)
                await RemoveFailedBuildAsync(server.Id, release, attempt);

            lock (server.Sync)
            {
                server.BuildingRelease = null;
                release.MarkFailed(failure is OperationCanceledException
                    ? "The build was cancelled."
                    : failure.Message);
            }
        }
    }

    private async Task<string> BuildAsync(Guid id, ReleaseRecord release, ServerSpec spec, byte[] archive,
        BuildAttempt attempt, CancellationToken ct)
    {
        var hash = ProjectArchive.EnvironmentHash(archive);
        release.SetEnvironmentHash(hash);
        attempt.CodeVolume = ContainerTemplate.CodeVolume(id, release.Number);

        await networks.EnsureAsync(id, spec.Network, ct);
        await EnsureVolumesAsync(id, release.Number, hash, ct);

        string? reused;
        try
        {
            reused = await UnpackAsync(id, release.Number, hash, archive, ct);
        }
        catch (BuilderFileTooLargeException)
        {
            attempt.EnvironmentVolume = ContainerTemplate.EnvironmentVolume(id, hash);
            throw;
        }

        if (reused is not null)
        {
            release.AppendLog($"Environment {hash} is already built, reusing it.\n");
            return reused;
        }

        attempt.EnvironmentVolume = ContainerTemplate.EnvironmentVolume(id, hash);
        return await RunBuildContainerAsync(id, release, hash, spec, ct);
    }

    private async Task EnsureVolumesAsync(Guid id, int number, string hash, CancellationToken ct)
    {
        string[] volumes =
        [
            ContainerTemplate.CodeVolume(id, number),
            ContainerTemplate.EnvironmentVolume(id, hash),
            ContainerTemplate.CacheVolume(id),
            ContainerTemplate.DataVolume(id)
        ];

        foreach (var volume in volumes)
            await engine.EnsureVolumeAsync(id, ContainerTemplate.Volume(id, volume), ct);
    }

    private async Task<string?> UnpackAsync(Guid id, int number, string hash, byte[] archive, CancellationToken ct)
    {
        var parameters = ContainerTemplate.Unpack(id, number, hash, options);
        await engine.RemoveContainerAsync(id, parameters.Name, ct);
        await engine.CreateContainerAsync(id, parameters, ct);
        try
        {
            await using var body = new MemoryStream(archive, false);
            await engine.ExtractArchiveAsync(id, parameters.Name, CodePath, body, ct);
            return await engine.ReadFileAsync(id, parameters.Name, LockFilePath, MaxLockFileBytes, ct);
        }
        finally
        {
            await engine.RemoveContainerAsync(id, parameters.Name, CancellationToken.None);
        }
    }

    private async Task<string> RunBuildContainerAsync(Guid id, ReleaseRecord release, string hash, ServerSpec spec,
        CancellationToken ct)
    {
        var parameters = ContainerTemplate.Build(id, release.Number, hash, spec, options);
        using var timeout = new CancellationTokenSource(BuildTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        await engine.RemoveContainerAsync(id, parameters.Name, ct);
        await engine.CreateContainerAsync(id, parameters, ct);
        try
        {
            await engine.StartContainerAsync(id, parameters.Name, ct);
            var logs = CollectLogsAsync(id, parameters.Name, release, linked.Token);
            var exitCode = await engine.WaitContainerAsync(id, parameters.Name, linked.Token);
            await logs;
            if (exitCode != 0)
                throw new InvalidOperationException($"Installing the dependencies failed with exit code {exitCode}.");

            var lockFile = await engine.ReadFileAsync(id, parameters.Name, LockFilePath, MaxLockFileBytes, ct)
                           ?? throw new InvalidOperationException("The build finished without writing a lock file.");
            await engine.RemoveContainerAsync(id, parameters.Name, ct);
            return lockFile;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The build did not finish within {BuildTimeout.TotalMinutes:0} minutes.");
        }
    }

    private async Task CollectLogsAsync(Guid id, string container, ReleaseRecord release, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in engine.ReadLogsAsync(id, container, null, true, ct)) release.AppendLog(chunk);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception failure)
        {
            logger.LogWarning("Reading the build log of {Container} failed: {Reason}", container, failure.Message);
        }
    }

    private async Task RemoveFailedBuildAsync(Guid id, ReleaseRecord release, BuildAttempt attempt)
    {
        try
        {
            await engine.RemoveContainerAsync(id, ContainerTemplate.BuildContainerName(id, release.Number),
                CancellationToken.None);
            if (attempt.CodeVolume is { } code)
                await engine.RemoveVolumeAsync(id, code, CancellationToken.None);

            if (attempt.EnvironmentVolume is { } environment)
                await engine.RemoveVolumeAsync(id, environment, CancellationToken.None);
        }
        catch (Exception failure)
        {
            logger.LogWarning("Cleaning up the failed build of release {Release} of server {Server} failed: {Reason}",
                release.Number, id, failure.Message);
        }
    }

    private sealed class BuildAttempt
    {
        public string? CodeVolume { get; set; }
        public string? EnvironmentVolume { get; set; }
    }
}
