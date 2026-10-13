using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed class UsageJournal(IServiceScopeFactory scopes, ILogger<UsageJournal> logger)
{
    public const int Capacity = 10_000;

    private const int BatchSize = 500;

    private readonly Channel<UsageRecord> _pending =
        Channel.CreateBounded<UsageRecord>(new BoundedChannelOptions(Capacity) { SingleReader = true });

    private long _dropped;

    public void Record(UsageRecord record)
    {
        if (!_pending.Writer.TryWrite(record))
            Interlocked.Increment(ref _dropped);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            logger.LogWarning("журнал вызовов не успевает записываться, пропущено записей: {Dropped}", dropped);

        var batch = new List<UsageRecord>(BatchSize);
        do
        {
            batch.Clear();
            while (batch.Count < BatchSize && _pending.Reader.TryRead(out var record))
                batch.Add(record);

            if (batch.Count == 0)
                return;

            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<UsageStore>().AppendAsync(batch, ct);
        }
        while (batch.Count == BatchSize);
    }
}
