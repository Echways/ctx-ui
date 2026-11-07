using System.Collections.Concurrent;
using System.Text;
using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Runner;

public sealed class RunnerRequestException(int status, string message, string? type = null) : Exception(message)
{
    public int Status { get; } = status;

    public string? Type { get; } = type;

    public static RunnerRequestException NotFound(string message)
    {
        return new RunnerRequestException(StatusCodes.Status404NotFound, message);
    }

    public static RunnerRequestException Conflict(string message)
    {
        return new RunnerRequestException(StatusCodes.Status409Conflict, message);
    }

    public static RunnerRequestException UnknownServer(Guid id)
    {
        return new RunnerRequestException(StatusCodes.Status404NotFound,
            $"Server {id} is not known. Send its specification with PUT first.",
            RunnerProblems.SpecRequired);
    }

    public static RunnerRequestException SpecMissing(Guid id)
    {
        return new RunnerRequestException(StatusCodes.Status409Conflict,
            $"The specification of server {id} is not known. Send it with PUT first.", RunnerProblems.SpecRequired);
    }
}

public sealed class ReleaseRecord(int number, ReleaseState state)
{
    public const int MaxLogCharacters = 256 * 1024;

    private readonly object _sync = new();
    private readonly StringBuilder _log = new();

    public int Number { get; } = number;
    public ReleaseState State { get; private set; } = state;
    public string? LockFile { get; private set; }
    public string? EnvironmentHash { get; private set; }
    public string? Error { get; private set; }
    public bool Runnable { get; private set; }

    public void AppendLog(string text)
    {
        lock (_sync)
        {
            _log.Append(text);
            if (_log.Length > MaxLogCharacters)
                _log.Remove(0, _log.Length - MaxLogCharacters);
        }
    }

    public void SetEnvironmentHash(string hash)
    {
        lock (_sync)
        {
            EnvironmentHash = hash;
        }
    }

    public void MarkBuilt(string lockFile)
    {
        lock (_sync)
        {
            LockFile = lockFile;
            Runnable = true;
            State = ReleaseState.Built;
            Error = null;
        }
    }

    public void MarkRunnable(ReleaseState state)
    {
        lock (_sync)
        {
            Runnable = true;
            State = state;
            Error = null;
        }
    }

    public void MarkState(ReleaseState state)
    {
        lock (_sync)
        {
            State = state;
        }
    }

    public void MarkFailed(string error)
    {
        lock (_sync)
        {
            State = ReleaseState.Failed;
            Error = error;
        }
    }

    public ReleaseStatus ToStatus()
    {
        lock (_sync)
        {
            return new ReleaseStatus(Number, State, _log.ToString(), LockFile, EnvironmentHash, Error);
        }
    }
}

public sealed class ServerRecord(Guid id, CancellationToken stopping)
{
    private readonly object _workSync = new();
    private readonly HashSet<Task> _work = [];

    public Guid Id { get; } = id;
    public object Sync { get; } = new();
    public SemaphoreSlim Operations { get; } = new(1, 1);
    public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    public ServerSpec? Spec { get; set; }
    public Dictionary<int, ReleaseRecord> Releases { get; } = [];
    public Dictionary<int, string> Candidates { get; } = [];
    public HashSet<string> Retiring { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Unmarked { get; } = new(StringComparer.Ordinal);
    public int? ActiveRelease { get; set; }
    public string? ActiveContainer { get; set; }
    public int? BuildingRelease { get; set; }
    public int? StartingRelease { get; set; }

    public void Track(Task work)
    {
        lock (_workSync)
        {
            _work.Add(work);
        }

        work.ContinueWith(completed =>
        {
            lock (_workSync)
            {
                _work.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task WorkFinishedAsync()
    {
        lock (_workSync)
        {
            return Task.WhenAll(_work.ToArray());
        }
    }

    public ServerState ToState()
    {
        lock (Sync)
        {
            return new ServerState(Id, ActiveRelease,
                Releases.Values.OrderBy(release => release.Number).Select(release => release.ToStatus()).ToList());
        }
    }

    public ReleaseRecord RequireRelease(int number)
    {
        lock (Sync)
        {
            return Releases.GetValueOrDefault(number)
                   ?? throw RunnerRequestException.NotFound($"Release {number} of server {Id} is not known.");
        }
    }
}

public sealed class ServerRegistry(CancellationToken stopping)
{
    private readonly ConcurrentDictionary<Guid, ServerRecord> _servers = new();

    public ServerRecord GetOrAdd(Guid id)
    {
        return _servers.GetOrAdd(id, key => new ServerRecord(key, stopping));
    }

    public ServerRecord? Find(Guid id)
    {
        return _servers.GetValueOrDefault(id);
    }

    public ServerRecord Require(Guid id)
    {
        return Find(id) ?? throw RunnerRequestException.UnknownServer(id);
    }

    public ServerRecord? Remove(Guid id)
    {
        return _servers.TryRemove(id, out var server) ? server : null;
    }
}
