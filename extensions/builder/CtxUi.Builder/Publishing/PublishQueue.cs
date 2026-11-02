using System.Threading.Channels;
using CtxUi.Builder.Data;
using CtxUi.Builder.Runner;
using CtxUi.Extensions;
using Enfilade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CtxUi.Builder.Publishing;

public sealed class PublishQueue : BackgroundService, IExtensionInitializer
{
    private sealed record Job(Guid ServerId, Func<CancellationToken, Task> Run);

    private readonly PublishPipeline _pipeline;
    private readonly PublishTimings _timings;
    private readonly RunnerSettings _settings;
    private readonly ReleaseArchives _archives;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PublishQueue> _log;
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<Job>> _lanes = [];
    private readonly Dictionary<Guid, int> _outstanding = [];
    private readonly HashSet<Guid> _preparing = [];

    public PublishQueue(PublishPipeline pipeline, RunnerSettings settings, PublishTimings timings, ReleaseArchives archives, IServiceScopeFactory scopes, ILogger<PublishQueue> log)
    {
        _pipeline = pipeline;
        _settings = settings;
        _timings = timings;
        _archives = archives;
        _scopes = scopes;
        _log = log;
        _pipeline.Changed += Raise;
    }

    public event Action<Guid>? Changed;

    public event Action<Guid>? FirstActivated;

    public bool IsBusy(Guid serverId)
    {
        lock (_gate)
            return _outstanding.ContainsKey(serverId);
    }

    public async Task<int> PublishAsync(Guid serverId, PublishMode mode, CancellationToken ct)
    {
        RequireConfigured();
        lock (_gate)
        {
            if (!_preparing.Add(serverId))
                throw new PanelException(BuilderText.PublishBusy);
        }

        try
        {
            var number = await _pipeline.PrepareAsync(serverId, ct);
            Enqueue(serverId, token => PublishAnnouncingAsync(serverId, number, mode, token));
            return number;
        }
        finally
        {
            lock (_gate)
                _preparing.Remove(serverId);
        }
    }

    public async Task RollbackAsync(Guid serverId, int release, CancellationToken ct)
    {
        RequireConfigured();
        await _pipeline.ValidateRollbackAsync(serverId, release, ct);
        Enqueue(serverId, token => _pipeline.RollbackAsync(serverId, release, token));
    }

    public Task ApplySettingsAsync(Guid serverId, CancellationToken ct)
    {
        Enqueue(serverId, token => _pipeline.ApplySettingsAsync(serverId, token));
        return Task.CompletedTask;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _archives.RemoveInterrupted();
        await _pipeline.MarkInterruptedAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lanes = new List<Task>();
        try
        {
            lanes.Add(RepairLoopAsync(stoppingToken));
            await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
                Dispatch(job, lanes, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            _log.LogError(failure, "The publication queue stopped unexpectedly.");
        }

        await Task.WhenAll(lanes);
    }

    private async Task RepairLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_timings.RepairInterval);
        try
        {
            do
            {
                await ScheduleRepairsAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ScheduleRepairsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<BuilderDatabase>();
            var servers = await database.Servers.AsNoTracking().Where(server => server.ActiveReleaseId != null).Select(server => server.Id).ToListAsync(ct);
            foreach (var serverId in servers.Where(serverId => !IsBusy(serverId)))
                Enqueue(serverId, token => _pipeline.RepairActivationAsync(serverId, token));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _log.LogError(failure, "The activation of published servers could not be checked.");
        }
    }

    private async Task PublishAnnouncingAsync(Guid serverId, int number, PublishMode mode, CancellationToken ct)
    {
        var hadActive = (await ConnectionStateAsync(serverId, ct)).Active;
        await _pipeline.PublishAsync(serverId, number, mode, ct);
        var after = await ConnectionStateAsync(serverId, ct);
        if (!hadActive && after.Active && !after.Connected)
            RaiseFirstActivated(serverId);
    }

    private async Task<(bool Active, bool Connected)> ConnectionStateAsync(Guid serverId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<BuilderDatabase>();
        var state = await database.Servers.AsNoTracking().Where(server => server.Id == serverId)
            .Select(server => new { server.ActiveReleaseId, server.InboundId }).SingleOrDefaultAsync(ct);
        return (state?.ActiveReleaseId is not null, state?.InboundId is not null);
    }

    private void RequireConfigured()
    {
        if (!_settings.IsComplete)
            throw new PanelException(string.Format(BuilderText.RunnerNotConfigured, string.Join(", ", _settings.Missing)));
    }

    private void Enqueue(Guid serverId, Func<CancellationToken, Task> run)
    {
        lock (_gate)
            _outstanding[serverId] = _outstanding.GetValueOrDefault(serverId) + 1;
        _jobs.Writer.TryWrite(new Job(serverId, run));
        Raise(serverId);
    }

    private void Dispatch(Job job, List<Task> lanes, CancellationToken stop)
    {
        lock (_gate)
        {
            if (_lanes.TryGetValue(job.ServerId, out var queue))
            {
                queue.Enqueue(job);
                return;
            }

            _lanes[job.ServerId] = new Queue<Job>([job]);
        }

        lanes.RemoveAll(lane => lane.IsCompleted);
        lanes.Add(Task.Run(() => RunLaneAsync(job.ServerId, stop), CancellationToken.None));
    }

    private async Task RunLaneAsync(Guid serverId, CancellationToken stop)
    {
        while (true)
        {
            Job? job;
            lock (_gate)
            {
                if (!_lanes[serverId].TryDequeue(out job))
                {
                    _lanes.Remove(serverId);
                    return;
                }
            }

            await RunJobAsync(job, stop);
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken stop)
    {
        try
        {
            stop.ThrowIfCancellationRequested();
            await job.Run(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            _log.LogError(failure, "A publication job of server {Server} failed unexpectedly.", job.ServerId);
        }
        finally
        {
            lock (_gate)
            {
                if (_outstanding[job.ServerId] <= 1)
                    _outstanding.Remove(job.ServerId);
                else
                    _outstanding[job.ServerId]--;
            }

            Raise(job.ServerId);
        }
    }

    private void RaiseFirstActivated(Guid serverId)
    {
        try
        {
            FirstActivated?.Invoke(serverId);
        }
        catch (Exception failure)
        {
            _log.LogWarning(failure, "A listener of first activations failed.");
        }
    }

    private void Raise(Guid serverId)
    {
        try
        {
            Changed?.Invoke(serverId);
        }
        catch (Exception failure)
        {
            _log.LogWarning(failure, "A listener of publication changes failed.");
        }
    }
}
