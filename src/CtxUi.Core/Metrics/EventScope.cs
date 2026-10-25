using System.Globalization;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Metrics;

internal enum CallGrouping
{
    All,
    Inbound,
    Tool,
    Method,
    Client,
    Device,
}

internal sealed record CallGroup(string? Key, string? Sub, CallStats Stats);

internal sealed class RefusalRow
{
    public Guid ClientId { get; init; }
    public UsageOutcome Outcome { get; init; }
    public int Count { get; init; }
}

internal sealed class EventScope
{
    private static readonly IReadOnlyList<int> LatencyBounds = [100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000];

    private static readonly string Forwarded = Outcomes(UsageRecord.IsForwarded);

    private static readonly string Problems = Outcomes(outcome => UsageRecord.IsForwarded(outcome) && outcome != UsageOutcome.Ok);

    private readonly CtxUiDbContext _db;
    private readonly Timeline _timeline;
    private readonly List<object> _arguments = [];
    private readonly string _from;
    private readonly string _events;
    private readonly string? _refusals;

    public EventScope(CtxUiDbContext db, MetricsFilter filter, Timeline timeline)
    {
        _db = db;
        _timeline = timeline;
        _from = Argument(timeline.From.ToUnixTimeMilliseconds());
        var until = Argument(timeline.Until.ToUnixTimeMilliseconds());
        var owners = new List<string>();
        if (filter.ClientId is { } clientId)
            owners.Add($"\"ClientId\" = {Argument(clientId)}");
        if (filter.InboundId is { } inboundId)
            owners.Add($"\"InboundId\" = {Argument(inboundId)}");

        var events = new List<string> { $"\"At\" >= {_from}", $"\"At\" < {until}" };
        events.AddRange(owners);
        if (filter.Tool is { } tool)
        {
            events.Add($"\"Method\" = '{UsageRecord.ToolsCall}'");
            events.Add($"\"Tool\" = {Argument(tool)}");
        }

        _events = string.Join(" AND ", events);
        _refusals = filter.Tool is null ? string.Join(" AND ", [$"\"Minute\" >= {_from}", $"\"Minute\" < {until}", .. owners]) : null;
    }

    public async Task<List<CallGroup>> CallsAsync(CallGrouping grouping, CancellationToken ct)
    {
        var (key, sub, only) = grouping switch
        {
            CallGrouping.All => ("NULL", "NULL", ""),
            CallGrouping.Inbound => ("\"InboundId\"", "NULL", ""),
            CallGrouping.Tool => ("\"InboundId\"", "\"Tool\"", $" AND \"Method\" = '{UsageRecord.ToolsCall}' AND \"Tool\" IS NOT NULL"),
            CallGrouping.Method => ("\"Method\"", "NULL", ""),
            CallGrouping.Client => ("\"ClientId\"", "NULL", ""),
            CallGrouping.Device => ("\"DeviceId\"", "NULL", " AND \"DeviceId\" IS NOT NULL"),
            _ => throw new ArgumentOutOfRangeException(nameof(grouping)),
        };
        var rows = await QueryAsync<CallRow>($"""
            WITH "Scoped" AS (
                SELECT {key} AS "Key", {sub} AS "Sub", "ClientId", "Method", "Outcome", "DurationMs", "RequestBytes", "ResponseBytes", "At"
                FROM "UsageEvents"
                WHERE {_events} AND "Outcome" IN ({Forwarded}){only}
            ), "Ranked" AS (
                SELECT *,
                    ROW_NUMBER() OVER (PARTITION BY "Key", "Sub" ORDER BY "DurationMs") AS "Rank",
                    COUNT(*) OVER (PARTITION BY "Key", "Sub") AS "Total"
                FROM "Scoped"
            )
            SELECT "Key", "Sub",
                COUNT(*) AS "Calls",
                COUNT(CASE WHEN "Method" = '{UsageRecord.ToolsCall}' THEN 1 END) AS "ToolCalls",
                COUNT(CASE WHEN "Outcome" = {(int)UsageOutcome.UpstreamFailed} THEN 1 END) AS "Failures",
                COUNT(CASE WHEN "Outcome" = {(int)UsageOutcome.UpstreamRejected} THEN 1 END) AS "Rejected",
                COUNT(CASE WHEN "Outcome" = {(int)UsageOutcome.ClientAborted} THEN 1 END) AS "Aborted",
                COUNT(DISTINCT "ClientId") AS "Clients",
                SUM("RequestBytes") AS "RequestBytes",
                SUM("ResponseBytes") AS "ResponseBytes",
                MIN(CASE WHEN "Rank" * 100 >= "Total" * 50 THEN "DurationMs" END) AS "P50",
                MIN(CASE WHEN "Rank" * 100 >= "Total" * 95 THEN "DurationMs" END) AS "P95",
                MIN(CASE WHEN "Rank" * 100 >= "Total" * 99 THEN "DurationMs" END) AS "P99",
                MAX("DurationMs") AS "Slowest",
                MAX("At") AS "LastAt"
            FROM "Ranked"
            GROUP BY "Key", "Sub"
            """, ct);

        return rows.Select(row => new CallGroup(row.Key, row.Sub, row.ToStats())).ToList();
    }

    public async Task<List<Slot>> SlotsAsync(CancellationToken ct)
    {
        var length = ((long)_timeline.Length.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        var rows = (await QueryAsync<SlotRow>($"""
                SELECT ("At" - {_from}) / {length} AS "Index",
                    COUNT(*) AS "Calls",
                    COUNT(CASE WHEN "Outcome" = {(int)UsageOutcome.UpstreamFailed} THEN 1 END) AS "Failures"
                FROM "UsageEvents"
                WHERE {_events} AND "Outcome" IN ({Forwarded})
                GROUP BY "Index"
                """, ct))
            .ToDictionary(row => row.Index);

        return Enumerable.Range(0, _timeline.Count)
            .Select(index => rows.TryGetValue(index, out var row)
                ? new Slot(_timeline.StartOf(index), row.Calls, row.Failures)
                : new Slot(_timeline.StartOf(index), 0, 0))
            .ToList();
    }

    public async Task<List<LatencyBucket>> LatencyAsync(CancellationToken ct)
    {
        var cases = string.Concat(LatencyBounds.Select((bound, index) =>
            $" WHEN \"DurationMs\" < {bound.ToString(CultureInfo.InvariantCulture)} THEN {index.ToString(CultureInfo.InvariantCulture)}"));
        var rows = (await QueryAsync<BucketRow>($"""
                SELECT CASE{cases} ELSE {LatencyBounds.Count} END AS "Bucket", COUNT(*) AS "Calls"
                FROM "UsageEvents"
                WHERE {_events} AND "Outcome" IN ({Forwarded})
                GROUP BY "Bucket"
                """, ct))
            .ToDictionary(row => row.Bucket, row => row.Calls);

        return Enumerable.Range(0, LatencyBounds.Count + 1)
            .Select(index => new LatencyBucket(
                index == 0 ? 0 : LatencyBounds[index - 1],
                index < LatencyBounds.Count ? LatencyBounds[index] : null,
                rows.GetValueOrDefault(index)))
            .ToList();
    }

    public async Task<Funnel> FunnelAsync(CancellationToken ct)
    {
        var row = (await QueryAsync<FunnelRow>($"""
            SELECT COUNT(*) AS "Seen",
                COALESCE(SUM("Initialized"), 0) AS "Initialized",
                COALESCE(SUM("Listed"), 0) AS "Listed",
                COALESCE(SUM("Called"), 0) AS "Called",
                COALESCE(SUM("Succeeded"), 0) AS "Succeeded"
            FROM (
                SELECT MAX("Method" = 'initialize') AS "Initialized",
                    MAX("Method" = 'tools/list') AS "Listed",
                    MAX("Method" = '{UsageRecord.ToolsCall}') AS "Called",
                    MAX("Method" = '{UsageRecord.ToolsCall}' AND "Outcome" = {(int)UsageOutcome.Ok}) AS "Succeeded"
                FROM "UsageEvents"
                WHERE {_events}
                GROUP BY "ClientId"
            )
            """, ct)).Single();

        return new Funnel(row.Seen, row.Initialized, row.Listed, row.Called, row.Succeeded);
    }

    public async Task<List<ErrorGroup>> ErrorsAsync(int limit, CancellationToken ct)
    {
        var rows = await QueryAsync<ErrorRow>($"""
            SELECT "InboundId", "Method", "Tool", "Outcome", "Status", COUNT(*) AS "Count", MAX("At") AS "LastAt"
            FROM "UsageEvents"
            WHERE {_events} AND "Outcome" IN ({Problems})
            GROUP BY "InboundId", "Method", "Tool", "Outcome", "Status"
            ORDER BY "Count" DESC, "LastAt" DESC
            LIMIT {limit.ToString(CultureInfo.InvariantCulture)}
            """, ct);

        return rows
            .Select(row => new ErrorGroup(
                row.InboundId, null, row.Method, row.Tool, row.Outcome, row.Status, row.Count, DateTimeOffset.FromUnixTimeMilliseconds(row.LastAt)))
            .ToList();
    }

    public Task<List<RefusalRow>> RefusalsAsync(CancellationToken ct)
    {
        var refusals = _refusals is null
            ? ""
            : $"""
                SELECT "ClientId", "Outcome", "Count" FROM "UsageRefusals" WHERE {_refusals}
                UNION ALL
                """;
        return QueryAsync<RefusalRow>($"""
            SELECT "ClientId", "Outcome", SUM("Count") AS "Count"
            FROM (
                {refusals}
                SELECT "ClientId", "Outcome", 1 AS "Count" FROM "UsageEvents" WHERE {_events} AND "Outcome" = {(int)UsageOutcome.QuotaExhausted}
            )
            GROUP BY "ClientId", "Outcome"
            """, ct);
    }

    private static string Outcomes(Func<UsageOutcome, bool> chosen) => string.Join(
        ", ", Enum.GetValues<UsageOutcome>().Where(chosen).Select(outcome => ((int)outcome).ToString(CultureInfo.InvariantCulture)));

    private string Argument(object value)
    {
        _arguments.Add(value);
        return "{" + (_arguments.Count - 1).ToString(CultureInfo.InvariantCulture) + "}";
    }

    private Task<List<T>> QueryAsync<T>(string sql, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<T>(sql, [.. _arguments]).ToListAsync(ct);

    private sealed class CallRow
    {
        public string? Key { get; init; }
        public string? Sub { get; init; }
        public int Calls { get; init; }
        public int ToolCalls { get; init; }
        public int Failures { get; init; }
        public int Rejected { get; init; }
        public int Aborted { get; init; }
        public int Clients { get; init; }
        public long RequestBytes { get; init; }
        public long ResponseBytes { get; init; }
        public int P50 { get; init; }
        public int P95 { get; init; }
        public int P99 { get; init; }
        public int Slowest { get; init; }
        public long LastAt { get; init; }

        public CallStats ToStats() => new(
            Calls,
            ToolCalls,
            Failures,
            Rejected,
            Aborted,
            Clients,
            RequestBytes,
            ResponseBytes,
            new Latency(P50, P95, P99, Slowest),
            DateTimeOffset.FromUnixTimeMilliseconds(LastAt));
    }

    private sealed class SlotRow
    {
        public long Index { get; init; }
        public int Calls { get; init; }
        public int Failures { get; init; }
    }

    private sealed class BucketRow
    {
        public int Bucket { get; init; }
        public int Calls { get; init; }
    }

    private sealed class FunnelRow
    {
        public int Seen { get; init; }
        public int Initialized { get; init; }
        public int Listed { get; init; }
        public int Called { get; init; }
        public int Succeeded { get; init; }
    }

    private sealed class ErrorRow
    {
        public Guid InboundId { get; init; }
        public required string Method { get; init; }
        public string? Tool { get; init; }
        public UsageOutcome Outcome { get; init; }
        public int Status { get; init; }
        public int Count { get; init; }
        public long LastAt { get; init; }
    }
}
