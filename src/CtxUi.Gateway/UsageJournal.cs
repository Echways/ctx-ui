using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed partial class UsageJournal(IServiceScopeFactory scopes, ILogger<UsageJournal> logger) : IDisposable
{
    public const int Capacity = 10_000;

    public const int MaxAttempts = 3;

    private const int BatchSize = 500;

    private readonly Channel<UsageRecord> _pending =
        Channel.CreateBounded<UsageRecord>(new BoundedChannelOptions(Capacity) { SingleReader = true });

    private readonly SemaphoreSlim _flushing = new(1, 1);

    private readonly List<UsageRecord> _batch = new(BatchSize);

    private int _failedAttempts;

    private long _dropped;

    public void Record(UsageRecord record)
    {
        if (!_pending.Writer.TryWrite(record))
            Interlocked.Increment(ref _dropped);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushing.WaitAsync(ct);
        try
        {
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                FallingBehind(logger, dropped);

            bool full;
            do
            {
                while (_batch.Count < BatchSize && _pending.Reader.TryRead(out var record))
                    _batch.Add(record);

                if (_batch.Count == 0)
                    return;

                full = _batch.Count == BatchSize;
                await SaveBatchAsync(ct);
            }
            while (full);
        }
        finally
        {
            _flushing.Release();
        }
    }

    public void Dispose() => _flushing.Dispose();

    private async Task SaveBatchAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<UsageStore>().AppendAsync(_batch, ct);
        }
        catch
        {
            if (++_failedAttempts == MaxAttempts)
            {
                BatchDropped(logger, MaxAttempts, _batch.Count);
                _batch.Clear();
                _failedAttempts = 0;
            }

            throw;
        }

        _batch.Clear();
        _failedAttempts = 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "usage journal is falling behind, records dropped: {Dropped}")]
    private static partial void FallingBehind(ILogger logger, long dropped);

    [LoggerMessage(Level = LogLevel.Error, Message = "usage journal batch failed {Attempts} times, records dropped: {Dropped}")]
    private static partial void BatchDropped(ILogger logger, int attempts, int dropped);
}
