using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;

namespace CtxUi.Core.Usage;

public sealed record InboundActivity(Guid Id, string Slug, string Name, bool Enabled, int Clients, int Calls, int Failures);

public sealed record Quarter(DateTimeOffset Start, int Calls, int Failures);

public sealed record Overview(
    int Clients,
    int ActiveClients,
    int SeenClients,
    int Calls,
    int ToolCalls,
    int Failures,
    IReadOnlyList<InboundActivity> Inbounds,
    IReadOnlyList<DailyTotal> Days,
    IReadOnlyList<Quarter> Quarters);

public sealed class OverviewService(CtxUiDbContext db, UsageStore usage, TimeProvider clock)
{
    public const int DaysShown = 7;

    public const int QuartersShown = 96;

    private static readonly TimeSpan Recent = TimeSpan.FromHours(24);

    private static readonly TimeSpan QuarterLength = TimeSpan.FromMinutes(15);

    public async Task<Overview> LoadAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var since = now - Recent;

        var clients = await db.Clients.AsNoTracking().ToListAsync(ct);
        var inbounds = await db.Inbounds.AsNoTracking().OrderBy(inbound => inbound.Slug).ToListAsync(ct);
        var links = await db.ClientInbounds.AsNoTracking()
            .GroupBy(link => link.InboundId)
            .Select(group => new { InboundId = group.Key, Clients = group.Count() })
            .ToDictionaryAsync(row => row.InboundId, row => row.Clients, ct);
        var calls = (await db.UsageEvents.AsNoTracking()
                .Where(call => call.At >= since)
                .GroupBy(call => new { call.InboundId, call.Outcome, IsToolCall = call.Method == UsageRecord.ToolsCall })
                .Select(group => new { group.Key.InboundId, group.Key.Outcome, group.Key.IsToolCall, Count = group.Count() })
                .ToListAsync(ct))
            .Where(row => UsageRecord.IsForwarded(row.Outcome))
            .ToList();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        return new Overview(
            clients.Count,
            clients.Count(client => ClientState.Of(client, now) == ClientStatus.Active),
            clients.Count(client => client.LastUsedAt >= since),
            calls.Sum(row => row.Count),
            calls.Where(row => row.IsToolCall).Sum(row => row.Count),
            calls.Where(row => row.Outcome == UsageOutcome.UpstreamFailed).Sum(row => row.Count),
            inbounds.Select(inbound => new InboundActivity(
                inbound.Id,
                inbound.Slug,
                inbound.Name,
                inbound.Enabled,
                links.GetValueOrDefault(inbound.Id),
                calls.Where(row => row.InboundId == inbound.Id).Sum(row => row.Count),
                calls.Where(row => row.InboundId == inbound.Id && row.Outcome == UsageOutcome.UpstreamFailed).Sum(row => row.Count)))
                .ToList(),
            await usage.DailyAsync(null, today.AddDays(1 - DaysShown), ct),
            await QuartersAsync(now, ct));
    }

    private async Task<List<Quarter>> QuartersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var current = new DateTimeOffset(now.UtcTicks - now.UtcTicks % QuarterLength.Ticks, TimeSpan.Zero);
        var first = current - QuarterLength * (QuartersShown - 1);
        var from = first.ToUnixTimeMilliseconds();
        var until = (current + QuarterLength).ToUnixTimeMilliseconds();
        var length = (long)QuarterLength.TotalMilliseconds;
        var rows = (await db.Database.SqlQuery<QuarterRow>($"""
                SELECT ("At" - {from}) / {length} AS "Slot", "Outcome", COUNT(*) AS "Count"
                FROM "UsageEvents"
                WHERE "At" >= {from} AND "At" < {until}
                GROUP BY "Slot", "Outcome"
                """).ToListAsync(ct))
            .Where(row => UsageRecord.IsForwarded(row.Outcome))
            .ToLookup(row => row.Slot);

        return Enumerable.Range(0, QuartersShown)
            .Select(slot => new Quarter(
                first + QuarterLength * slot,
                rows[slot].Sum(row => row.Count),
                rows[slot].Where(row => row.Outcome == UsageOutcome.UpstreamFailed).Sum(row => row.Count)))
            .ToList();
    }

    private sealed class QuarterRow
    {
        public long Slot { get; init; }
        public UsageOutcome Outcome { get; init; }
        public int Count { get; init; }
    }
}
