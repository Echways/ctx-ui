using System.Net;
using System.Security.Cryptography;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using Enfilade;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CtxUi.Builder.Publishing;

public sealed class PublishPipeline(
    IServiceScopeFactory scopes,
    RunnerSettings settings,
    ReleaseArchives archives,
    IToolInspector inspector,
    PublishTimings timings,
    TimeProvider clock,
    ILogger<PublishPipeline> log)
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private sealed class ReleaseFailure(string message) : Exception(message);

    private sealed class Run(AsyncServiceScope scope, Guid serverId) : IAsyncDisposable
    {
        public Guid ServerId { get; } = serverId;

        public BuilderDatabase Database { get; } = scope.ServiceProvider.GetRequiredService<BuilderDatabase>();

        public RunnerClient Runner { get; } = scope.ServiceProvider.GetRequiredService<RunnerClient>();

        public ServerCatalog Catalog { get; } = scope.ServiceProvider.GetRequiredService<ServerCatalog>();

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    public event Action<Guid>? Changed;

    public async Task<int> PrepareAsync(Guid serverId, CancellationToken ct)
    {
        await using var run = Open(serverId);
        if (await HasBuildingReleaseAsync(run, ct))
            throw new PanelException(BuilderText.PublishBusy);

        var draft = await run.Catalog.DraftAsync(serverId, ct);
        if (draft.OpenApiJson is not null && string.IsNullOrWhiteSpace(draft.BaseUrl))
            throw new PanelException(BuilderText.PublishBaseUrlRequired);

        ServerState state;
        try
        {
            await run.Runner.PutServerAsync(serverId, (await run.Catalog.LaunchAsync(serverId, ct)).Spec, ct);
            state = await run.Runner.ServerAsync(serverId, ct);
        }
        catch (RunnerException failure)
        {
            throw new PanelException(Describe(failure));
        }

        if (state.Releases.Any(release => release.State == ReleaseState.Building))
            throw new PanelException(BuilderText.PublishBusy);

        byte[] archive;
        try
        {
            archive = await ProjectFiles.SnapshotAsync(draft, draft.Files, ProjectFiles.FastMcpVersion, ct);
        }
        catch (ProjectFileException failure)
        {
            throw new PanelException(failure.Message);
        }

        var stored = await run.Database.Releases.Where(release => release.ServerId == serverId).MaxAsync(release => (int?)release.Number, ct) ?? 0;
        var number = Math.Max(stored, state.Releases.Select(release => release.Number).DefaultIfEmpty(0).Max()) + 1;
        var created = new Release
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            Number = number,
            Status = ReleaseState.Building,
            FastMcpVersion = ProjectFiles.FastMcpVersion,
            ArchiveHash = Convert.ToHexString(SHA256.HashData(archive)),
            CreatedAt = clock.GetUtcNow(),
        };
        await archives.WriteAsync(created.Id, archive, ct);
        run.Database.Releases.Add(created);
        await run.Database.SaveChangesAsync(ct);
        return number;
    }

    public async Task ValidateRollbackAsync(Guid serverId, int number, CancellationToken ct)
    {
        await using var run = Open(serverId);
        if (await HasBuildingReleaseAsync(run, ct))
            throw new PanelException(BuilderText.PublishBusy);

        var activeId = await run.Database.Servers.Where(server => server.Id == serverId).Select(server => server.ActiveReleaseId).SingleOrDefaultAsync(ct);
        var release = await run.Database.Releases.AsNoTracking().SingleOrDefaultAsync(release => release.ServerId == serverId && release.Number == number, ct)
            ?? throw new PanelException(BuilderText.ReleaseNotFound);
        if (release.Id == activeId || release.Status is not (ReleaseState.Retired or ReleaseState.Candidate))
            throw new PanelException(BuilderText.RollbackUnavailable);
    }

    public async Task MarkInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<BuilderDatabase>();
        var building = await database.Releases.AsNoTracking().Where(release => release.Status == ReleaseState.Building).OrderBy(release => release.ServerId).ToListAsync(ct);
        var runnerReachable = settings.IsComplete;
        foreach (var release in building)
        {
            await using var run = Open(release.ServerId);
            var activated = false;
            if (runnerReachable)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(LookupTimeout);
                try
                {
                    activated = (await run.Runner.ServerAsync(release.ServerId, limit.Token)).ActiveRelease == release.Number;
                }
                catch (Exception failure) when (failure is RunnerException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    runnerReachable = false;
                }
            }

            if (activated)
                await PromoteAsync(run, release.Id, ct);
            else
                await FailAsync(run, release.Number, BuilderText.PublishInterrupted, ct);
        }
    }

    public async Task RepairActivationAsync(Guid serverId, CancellationToken ct)
    {
        if (!settings.IsComplete)
            return;

        await using var run = Open(serverId);
        var active = await ActiveReleaseAsync(run, ct);
        if (active is null)
            return;

        try
        {
            var state = await run.Runner.ServerAsync(serverId, ct);
            if (state.ActiveRelease is null && state.Releases.Any(release => release.Number == active.Number && release.State == ReleaseState.Candidate))
                await run.Runner.ActivateReleaseAsync(serverId, active.Number, ct);
        }
        catch (RunnerException failure)
        {
            log.LogWarning("The activation of release {Number} of server {Server} could not be checked: {Message}", active.Number, serverId, failure.Message);
        }
    }

    public async Task PublishAsync(Guid serverId, int number, PublishMode mode, CancellationToken ct)
    {
        await using var run = Open(serverId);
        try
        {
            var release = await run.Database.Releases.AsNoTracking().SingleAsync(release => release.ServerId == serverId && release.Number == number, ct);
            await BuildAsync(run, release, ct);
            var tools = await StartAndCheckAsync(run, number, ct);
            await UpdateAsync(run, number, setters => setters.SetProperty(entity => entity.ToolsSnapshotJson, ToolSnapshot.Serialize(tools)), ct);
            if (mode == PublishMode.BuildOnly)
                await UpdateAsync(run, number, setters => setters.SetProperty(entity => entity.Status, ReleaseState.Candidate).SetProperty(entity => entity.FinishedAt, clock.GetUtcNow()), ct);
            else
            {
                await ActivateOnRunnerAsync(run, number, ct);
                await PromoteAsync(run, release.Id, ct);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(run, number, MessageOf(failure), ct);
        }
        finally
        {
            Raise(serverId);
        }
    }

    public async Task RollbackAsync(Guid serverId, int number, CancellationToken ct)
    {
        await using var run = Open(serverId);
        var release = await run.Database.Releases.AsNoTracking().SingleOrDefaultAsync(release => release.ServerId == serverId && release.Number == number, ct);
        if (release is null)
            return;
        await ActivateStoredAsync(run, release, ct);
    }

    public async Task ApplySettingsAsync(Guid serverId, CancellationToken ct)
    {
        if (!settings.IsComplete)
            return;

        await using var run = Open(serverId);
        var active = await ActiveReleaseAsync(run, ct);
        if (active is null)
            return;
        await ActivateStoredAsync(run, active, ct);
    }

    private async Task ActivateStoredAsync(Run run, Release release, CancellationToken ct)
    {
        try
        {
            await PutSpecAsync(run, ct);
            var state = await run.Runner.ServerAsync(run.ServerId, ct);
            var known = state.Releases.FirstOrDefault(entry => entry.Number == release.Number);
            if (known is null || known is { State: ReleaseState.Failed, EnvironmentHash: null } || known.State == ReleaseState.Building)
                await BuildAsync(run, release, ct);
            await StartAndCheckAsync(run, release.Number, ct);
            await ActivateOnRunnerAsync(run, release.Number, ct);
            await PromoteAsync(run, release.Id, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await UpdateAsync(run, release.Number, setters => setters.SetProperty(entity => entity.Error, MessageOf(failure)), ct);
        }
        finally
        {
            Raise(run.ServerId);
        }
    }

    private async Task BuildAsync(Run run, Release release, CancellationToken ct)
    {
        var launch = await PutSpecAsync(run, ct);
        await SendAsync(run, launch.Spec, async () =>
        {
            await using var archive = await archives.OpenVerifiedAsync(release, ct);
            await run.Runner.BuildReleaseAsync(run.ServerId, release.Number, archive, ct);
        }, ct);
        Raise(run.ServerId);

        var status = await PollAsync(run, release.Number, status => status.State != ReleaseState.Building, timings.BuildTimeout, BuilderText.BuildTimedOut, true, ct);
        await StoreBuildAsync(run, release.Number, status, ct);
        if (status.State == ReleaseState.Failed)
            throw new ReleaseFailure(status.Error ?? BuilderText.BuildFailed);
    }

    private async Task<IReadOnlyList<ToolInfo>> StartAndCheckAsync(Run run, int number, CancellationToken ct)
    {
        var launch = await PutSpecAsync(run, ct);
        await SendAsync(run, launch.Spec, () => run.Runner.StartReleaseAsync(run.ServerId, number, launch.Start, ct), ct);
        var status = await PollAsync(run, number, status => status.State is ReleaseState.Candidate or ReleaseState.Failed, timings.StartTimeout, BuilderText.StartTimedOut, false, ct);
        if (status.State == ReleaseState.Failed)
            throw new ReleaseFailure(status.Error ?? BuilderText.StartFailed);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timings.CheckTimeout);
        try
        {
            return await inspector.ListAsync(run.ServerId, number, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ReleaseFailure(string.Format(BuilderText.CheckTimedOut, Seconds(timings.CheckTimeout)));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw new ReleaseFailure(string.Format(BuilderText.CheckFailed, failure.Message));
        }
    }

    private async Task ActivateOnRunnerAsync(Run run, int number, CancellationToken ct)
    {
        try
        {
            await run.Runner.ActivateReleaseAsync(run.ServerId, number, ct);
        }
        catch (RunnerException failure)
        {
            throw new ReleaseFailure(Describe(failure));
        }
    }

    private async Task<ServerLaunch> PutSpecAsync(Run run, CancellationToken ct)
    {
        var launch = await run.Catalog.LaunchAsync(run.ServerId, ct);
        await run.Runner.PutServerAsync(run.ServerId, launch.Spec, ct);
        return launch;
    }

    private static async Task SendAsync(Run run, ServerSpec spec, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await send();
        }
        catch (RunnerException failure) when (failure.Status == HttpStatusCode.Conflict && failure.Message.Contains("PUT", StringComparison.Ordinal))
        {
            await run.Runner.PutServerAsync(run.ServerId, spec, ct);
            await send();
        }
    }

    private async Task<ReleaseStatus> PollAsync(Run run, int number, Func<ReleaseStatus, bool> done, TimeSpan timeout, string timeoutMessage, bool recordLog, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        long? outageStarted = null;
        var lastLog = string.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var status = await run.Runner.ReleaseAsync(run.ServerId, number, ct);
                outageStarted = null;
                if (recordLog && status.Log.Length > 0 && status.Log != lastLog)
                {
                    lastLog = status.Log;
                    await UpdateAsync(run, number, setters => setters.SetProperty(entity => entity.BuildLog, status.Log), ct);
                    Raise(run.ServerId);
                }

                if (done(status))
                    return status;
            }
            catch (RunnerException failure) when (failure.Status == HttpStatusCode.NotFound)
            {
                throw new ReleaseFailure(BuilderText.RunnerForgotRelease);
            }
            catch (RunnerException failure) when (failure.Status is null)
            {
                outageStarted ??= clock.GetTimestamp();
                if (clock.GetElapsedTime(outageStarted.Value) > timings.OutageTolerance)
                    throw new ReleaseFailure(string.Format(BuilderText.RunnerLostDuringBuild, Seconds(timings.OutageTolerance)));
            }

            if (clock.GetElapsedTime(started) > timeout)
                throw new ReleaseFailure(string.Format(timeoutMessage, Math.Max(1, (int)Math.Ceiling(timeout.TotalMinutes))));
            await Task.Delay(timings.PollInterval, clock, ct);
        }
    }

    private async Task StoreBuildAsync(Run run, int number, ReleaseStatus status, CancellationToken ct)
    {
        if (status.Log.Length == 0 && status.LockFile is null && status.EnvironmentHash is null)
            return;
        await UpdateAsync(run, number, setters =>
        {
            if (status.Log.Length > 0)
                setters.SetProperty(entity => entity.BuildLog, status.Log);
            if (status.LockFile is not null)
                setters.SetProperty(entity => entity.LockFile, status.LockFile);
            if (status.EnvironmentHash is not null)
                setters.SetProperty(entity => entity.EnvironmentHash, status.EnvironmentHash);
        }, ct);
    }

    private async Task PromoteAsync(Run run, Guid releaseId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await run.Database.Database.BeginTransactionAsync(ct);
        await run.Database.Releases
            .Where(release => release.ServerId == run.ServerId && release.Id != releaseId && (release.Status == ReleaseState.Active || release.Status == ReleaseState.Candidate))
            .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.Status, ReleaseState.Retired), ct);
        await run.Database.Releases.Where(release => release.Id == releaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(release => release.Status, ReleaseState.Active).SetProperty(release => release.FinishedAt, now).SetProperty(release => release.Error, (string?)null), ct);
        await run.Database.Servers.Where(server => server.Id == run.ServerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(server => server.ActiveReleaseId, releaseId), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task FailAsync(Run run, int number, string message, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            await UpdateAsync(run, number, setters => setters.SetProperty(entity => entity.Status, ReleaseState.Failed).SetProperty(entity => entity.Error, message).SetProperty(entity => entity.FinishedAt, now), ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            log.LogError(failure, "Release {Number} of server {Server} could not be marked as failed.", number, run.ServerId);
        }
        finally
        {
            Raise(run.ServerId);
        }
    }

    private static Task UpdateAsync(Run run, int number, Action<UpdateSettersBuilder<Release>> set, CancellationToken ct) =>
        run.Database.Releases.Where(release => release.ServerId == run.ServerId && release.Number == number).ExecuteUpdateAsync(set, ct);

    private static async Task<Release?> ActiveReleaseAsync(Run run, CancellationToken ct)
    {
        var activeId = await run.Database.Servers.Where(server => server.Id == run.ServerId).Select(server => server.ActiveReleaseId).SingleOrDefaultAsync(ct);
        return activeId is null ? null : await run.Database.Releases.AsNoTracking().SingleOrDefaultAsync(release => release.Id == activeId, ct);
    }

    private static Task<bool> HasBuildingReleaseAsync(Run run, CancellationToken ct) =>
        run.Database.Releases.AnyAsync(release => release.ServerId == run.ServerId && release.Status == ReleaseState.Building, ct);

    private Run Open(Guid serverId) => new(scopes.CreateAsyncScope(), serverId);

    private string Describe(RunnerException failure) =>
        failure.Status is null ? string.Format(BuilderText.RunnerUnreachable, settings.Url, failure.Message) : failure.Message;

    private string MessageOf(Exception failure)
    {
        switch (failure)
        {
            case ReleaseFailure or PanelException:
                return failure.Message;
            case RunnerException runner:
                return Describe(runner);
            default:
                log.LogError(failure, "A publication step failed unexpectedly.");
                return failure.Message;
        }
    }

    private static int Seconds(TimeSpan span) => Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));

    private void Raise(Guid serverId)
    {
        try
        {
            Changed?.Invoke(serverId);
        }
        catch (Exception failure)
        {
            log.LogWarning(failure, "A listener of publication changes failed.");
        }
    }
}
