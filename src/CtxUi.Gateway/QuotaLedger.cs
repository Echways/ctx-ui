using System.Collections.Concurrent;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

public readonly record struct QuotaCharge(bool Accepted, DateTimeOffset PeriodStart, int Calls, int? Limit, DateTimeOffset? ResetsAt);

public sealed class QuotaLedger
{
    private readonly ConcurrentDictionary<Guid, Counter> _counters = new();

    public void Restore(IEnumerable<StoredUsage> stored)
    {
        foreach (var usage in stored)
            _counters[usage.ClientId] = new Counter { Since = usage.Since, Used = usage.Calls };
    }

    public QuotaCharge Charge(ClientAccess client, int calls, DateTimeOffset now)
    {
        var limits = client.Limits;
        var start = QuotaPeriod.StartAt(client.PeriodStartedAt, limits.ResetEveryDays, now);
        var resetsAt = QuotaPeriod.NextReset(client.PeriodStartedAt, limits.ResetEveryDays, now);
        var counter = _counters.GetOrAdd(client.Id, _ => new Counter());
        lock (counter.Gate)
        {
            if (counter.Since != start)
            {
                counter.Since = start;
                counter.Used = 0;
                counter.Dirty = true;
            }

            if (limits.QuotaCalls is { } quota && counter.Used + calls > quota)
                return new QuotaCharge(false, start, calls, quota, resetsAt);

            counter.Used += calls;
            counter.Dirty = true;
            return new QuotaCharge(true, start, calls, limits.QuotaCalls, resetsAt);
        }
    }

    public void Refund(Guid clientId, QuotaCharge charge)
    {
        if (!charge.Accepted || !_counters.TryGetValue(clientId, out var counter))
            return;

        lock (counter.Gate)
        {
            if (counter.Since != charge.PeriodStart)
                return;

            counter.Used = Math.Max(0, counter.Used - charge.Calls);
            counter.Dirty = true;
        }
    }

    public async Task FlushAsync(Func<IReadOnlyCollection<StoredUsage>, Task> save)
    {
        var changed = new List<(Counter Counter, StoredUsage Usage)>();
        foreach (var (clientId, counter) in _counters)
        {
            lock (counter.Gate)
            {
                if (!counter.Dirty || counter.Since is not { } since)
                    continue;

                counter.Dirty = false;
                changed.Add((counter, new StoredUsage(clientId, since, counter.Used)));
            }
        }

        if (changed.Count == 0)
            return;

        try
        {
            await save(changed.Select(item => item.Usage).ToList());
        }
        catch
        {
            foreach (var (counter, _) in changed)
            {
                lock (counter.Gate)
                    counter.Dirty = true;
            }

            throw;
        }
    }

    private sealed class Counter
    {
        public Lock Gate { get; } = new();

        public DateTimeOffset? Since { get; set; }

        public long Used { get; set; }

        public bool Dirty { get; set; }
    }
}
