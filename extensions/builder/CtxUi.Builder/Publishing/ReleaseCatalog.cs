using System.Net;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Runner;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Publishing;

public enum ServerRunState { NoRelease, Running, Building, RunnerUnavailable, NeedsReactivation }

public sealed record ServerStatus(ServerRunState State, int? ActiveNumber, int? BuildingNumber);

public sealed record ReleaseView(
    int Number,
    ReleaseState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    bool IsActive,
    bool CanRollback,
    string? Error,
    string BuildLog,
    string? FastMcpVersion,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> RemovedTools);

public sealed class ReleaseCatalog(BuilderDatabase database, RunnerSettings settings, RunnerClient runner)
{
    private static readonly TimeSpan RunnerTimeout = TimeSpan.FromSeconds(5);

    public async Task<ServerStatus> StatusAsync(Guid serverId, CancellationToken ct)
    {
        var activeId = await database.Servers.AsNoTracking().Where(server => server.Id == serverId).Select(server => server.ActiveReleaseId).SingleOrDefaultAsync(ct);
        var numbers = await database.Releases.AsNoTracking().Where(release => release.ServerId == serverId && (release.Id == activeId || release.Status == ReleaseState.Building))
            .Select(release => new { release.Id, release.Number, release.Status }).ToListAsync(ct);
        var active = numbers.Where(release => release.Id == activeId).Select(release => (int?)release.Number).SingleOrDefault();
        var building = numbers.Where(release => release.Status == ReleaseState.Building).Select(release => (int?)release.Number).FirstOrDefault();

        if (building is not null)
            return new ServerStatus(ServerRunState.Building, active, building);
        if (active is null)
            return new ServerStatus(ServerRunState.NoRelease, null, null);

        return new ServerStatus(await RunnerRoutesAsync(serverId, active.Value, ct), active, null);
    }

    public async Task<IReadOnlyList<ReleaseView>> ListAsync(Guid serverId, CancellationToken ct)
    {
        var activeId = await database.Servers.AsNoTracking().Where(server => server.Id == serverId).Select(server => server.ActiveReleaseId).SingleOrDefaultAsync(ct);
        var releases = await database.Releases.AsNoTracking().Where(release => release.ServerId == serverId).OrderBy(release => release.Number).ToListAsync(ct);

        var views = new List<ReleaseView>(releases.Count);
        IReadOnlyList<string>? previous = null;
        foreach (var release in releases)
        {
            var tools = ToolSnapshot.Names(release.ToolsSnapshotJson);
            var removed = previous is null || release.ToolsSnapshotJson is null ? [] : previous.Except(tools).ToList();
            if (release.ToolsSnapshotJson is not null)
                previous = tools;
            var isActive = release.Id == activeId;
            views.Add(new ReleaseView(
                release.Number,
                release.Status,
                release.CreatedAt,
                release.FinishedAt,
                isActive,
                !isActive && release.Status is ReleaseState.Retired or ReleaseState.Candidate,
                release.Error,
                release.BuildLog ?? string.Empty,
                release.FastMcpVersion,
                tools,
                removed));
        }

        views.Reverse();
        return views;
    }

    private async Task<ServerRunState> RunnerRoutesAsync(Guid serverId, int active, CancellationToken ct)
    {
        if (!settings.IsComplete)
            return ServerRunState.RunnerUnavailable;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RunnerTimeout);
        try
        {
            var state = await runner.ServerAsync(serverId, limit.Token);
            return state.ActiveRelease == active ? ServerRunState.Running : ServerRunState.NeedsReactivation;
        }
        catch (RunnerException failure) when (failure.Status == HttpStatusCode.NotFound)
        {
            return ServerRunState.NeedsReactivation;
        }
        catch (Exception failure) when (failure is RunnerException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ServerRunState.RunnerUnavailable;
        }
    }
}
