using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;

namespace CtxUi.Core.Usage;

public sealed class UsageStore(CtxUiDbContext db)
{
    public async Task AppendAsync(IReadOnlyCollection<UsageRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
            return;

        foreach (var record in records.Where(record => record.Method is not null))
        {
            db.UsageEvents.Add(new UsageEvent
            {
                ClientId = record.ClientId,
                InboundId = record.InboundId,
                At = record.At,
                Method = record.Method!,
                Tool = record.Tool,
                DurationMs = record.DurationMs,
                Outcome = record.Outcome,
            });
        }

        var days = records.Select(record => record.Day).Distinct().ToList();
        var clientIds = records.Select(record => record.ClientId).Distinct().ToList();
        var known = (await db.UsageDays
                .Where(daily => days.Contains(daily.Day) && clientIds.Contains(daily.ClientId))
                .ToListAsync(ct))
            .ToDictionary(daily => (daily.Day, daily.ClientId, daily.InboundId));

        foreach (var record in records)
        {
            var key = (record.Day, record.ClientId, record.InboundId);
            if (!known.TryGetValue(key, out var daily))
            {
                daily = new UsageDaily { Day = record.Day, ClientId = record.ClientId, InboundId = record.InboundId };
                known.Add(key, daily);
                db.UsageDays.Add(daily);
            }

            if (!record.Forwarded)
            {
                daily.Refused++;
                continue;
            }

            daily.Calls++;
            daily.DurationMs += record.DurationMs;
            if (record.Method == UsageRecord.ToolsCall)
                daily.ToolCalls++;
            if (record.Outcome == UsageOutcome.UpstreamFailed)
                daily.Errors++;
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
        db.UsageEvents.Where(usage => usage.At < olderThan).ExecuteDeleteAsync(ct);

    public Task<List<UsageEvent>> RecentAsync(Guid? clientId, int limit, CancellationToken ct = default)
    {
        var events = db.UsageEvents.AsNoTracking();
        if (clientId is { } id)
            events = events.Where(usage => usage.ClientId == id);

        return events.OrderByDescending(usage => usage.Id).Take(limit).ToListAsync(ct);
    }

    public Task<List<DailyTotal>> DailyAsync(Guid? clientId, DateOnly from, CancellationToken ct = default)
    {
        var days = db.UsageDays.AsNoTracking().Where(daily => daily.Day >= from);
        if (clientId is { } id)
            days = days.Where(daily => daily.ClientId == id);

        return days
            .GroupBy(daily => daily.Day)
            .OrderBy(group => group.Key)
            .Select(group => new DailyTotal(
                group.Key,
                group.Sum(daily => daily.Calls),
                group.Sum(daily => daily.ToolCalls),
                group.Sum(daily => daily.Errors),
                group.Sum(daily => daily.Refused),
                group.Sum(daily => daily.DurationMs)))
            .ToListAsync(ct);
    }
}
