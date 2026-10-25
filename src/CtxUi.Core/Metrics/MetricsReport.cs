using CtxUi.Core.Usage;

namespace CtxUi.Core.Metrics;

public sealed record Latency(int P50, int P95, int P99, int Slowest)
{
    public static readonly Latency None = new(0, 0, 0, 0);
}

public sealed record CallStats(
    int Calls,
    int ToolCalls,
    int Failures,
    int Rejected,
    int Aborted,
    int Clients,
    long RequestBytes,
    long ResponseBytes,
    Latency Latency,
    DateTimeOffset? LastAt)
{
    public static readonly CallStats None = new(0, 0, 0, 0, 0, 0, 0, 0, Latency.None, null);

    public int Errors => Failures + Rejected;

    public double ErrorShare => Calls == 0 ? 0 : (double)Errors / Calls;
}

public sealed record Funnel(int Seen, int Initialized, int Listed, int Called, int Succeeded);

public sealed record LatencyBucket(int FromMs, int? UntilMs, int Calls);

public sealed record InboundStats(Guid Id, string? Slug, string? Name, CallStats Stats);

public sealed record ToolStats(Guid InboundId, string? Slug, string Tool, CallStats Stats);

public sealed record MethodStats(string Method, CallStats Stats);

public sealed record ClientStats(Guid Id, string? Name, int Refusals, CallStats Stats);

public sealed record DeviceStats(Guid Id, string? Name, IReadOnlyList<string> Apps, CallStats Stats);

public sealed record ErrorGroup(
    Guid InboundId,
    string? Slug,
    string Method,
    string? Tool,
    UsageOutcome Outcome,
    int Status,
    int Count,
    DateTimeOffset LastAt);

public sealed record RefusalCount(UsageOutcome Outcome, int Count);

public sealed record MetricsSubject(string? Client, string? Inbound, string? Tool);

public sealed record MetricsReport(
    MetricsPeriod Period,
    IReadOnlyList<int> Periods,
    DateTimeOffset From,
    MetricsFilter Filter,
    MetricsSubject Subject,
    CallStats Totals,
    int? ClientsWithAccess,
    Funnel Funnel,
    IReadOnlyList<Slot> Slots,
    IReadOnlyList<LatencyBucket> Latency,
    IReadOnlyList<InboundStats> Inbounds,
    IReadOnlyList<ToolStats> Tools,
    IReadOnlyList<MethodStats> Methods,
    IReadOnlyList<ClientStats> Clients,
    IReadOnlyList<DeviceStats> Devices,
    IReadOnlyList<ErrorGroup> Errors,
    IReadOnlyList<RefusalCount> Refusals)
{
    public int RefusalTotal => Refusals.Sum(refusal => refusal.Count);
}
