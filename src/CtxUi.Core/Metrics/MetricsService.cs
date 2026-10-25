using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Metrics;

public sealed class MetricsService(CtxUiDbContext db, IOptions<UsageOptions> options, TimeProvider clock)
{
    public const int ErrorsShown = 20;

    public IReadOnlyList<MetricsPeriod> Periods => MetricsPeriod.Within(options.Value.UsageRetentionDays);

    public MetricsPeriod Resolve(int? days)
    {
        var periods = Periods;
        if (days is null)
            return periods.LastOrDefault(period => period.Days <= MetricsPeriod.DefaultDays) ?? periods[0];

        return periods.FirstOrDefault(period => period.Days == days)
            ?? throw new DomainException(string.Format(CoreText.MetricsDaysInvalid, string.Join(", ", periods.Select(period => period.Days))));
    }

    public async Task<MetricsReport> LoadAsync(MetricsFilter filter, int? days = null, CancellationToken ct = default)
    {
        var period = Resolve(days);
        var now = clock.GetUtcNow();
        var timeline = period.TimelineAt(now);
        var scope = new EventScope(db, filter, timeline);

        var clients = await db.Clients.AsNoTracking().Include(client => client.Inbounds).ToDictionaryAsync(client => client.Id, ct);
        var inbounds = await db.Inbounds.AsNoTracking().ToDictionaryAsync(inbound => inbound.Id, ct);
        var refusals = await scope.RefusalsAsync(ct);
        var refusedByClient = refusals.GroupBy(row => row.ClientId).ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        string? Slug(Guid id) => inbounds.GetValueOrDefault(id)?.Slug;

        return new MetricsReport(
            period,
            Periods.Select(known => known.Days).ToList(),
            timeline.From,
            filter,
            new MetricsSubject(
                filter.ClientId is { } clientId ? clients.GetValueOrDefault(clientId)?.Name : null,
                filter.InboundId is { } inboundId ? Slug(inboundId) : null,
                filter.Tool),
            (await scope.CallsAsync(CallGrouping.All, ct)).SingleOrDefault()?.Stats ?? CallStats.None,
            filter.ClientId is null ? ClientsWithAccess(clients.Values, filter.InboundId, now) : null,
            await scope.FunnelAsync(ct),
            await scope.SlotsAsync(ct),
            await scope.LatencyAsync(ct),
            filter.InboundId is null
                ? Busiest(await scope.CallsAsync(CallGrouping.Inbound, ct), group => Guid.Parse(group.Key!), (id, stats) =>
                    new InboundStats(id, Slug(id), inbounds.GetValueOrDefault(id)?.Name, stats))
                : [],
            filter.Tool is null
                ? Busiest(await scope.CallsAsync(CallGrouping.Tool, ct), group => (Inbound: Guid.Parse(group.Key!), Tool: group.Sub!), (key, stats) =>
                    new ToolStats(key.Inbound, Slug(key.Inbound), key.Tool, stats))
                : [],
            filter.Tool is null
                ? Busiest(await scope.CallsAsync(CallGrouping.Method, ct), group => group.Key!, (method, stats) => new MethodStats(method, stats))
                : [],
            filter.ClientId is null
                ? Busiest(await scope.CallsAsync(CallGrouping.Client, ct), group => Guid.Parse(group.Key!), (id, stats) =>
                    new ClientStats(id, clients.GetValueOrDefault(id)?.Name, refusedByClient.GetValueOrDefault(id), stats))
                : [],
            filter.ClientId is not null ? await DevicesAsync(scope, ct) : [],
            (await scope.ErrorsAsync(ErrorsShown, ct)).Select(error => error with { Slug = Slug(error.InboundId) }).ToList(),
            refusals
                .GroupBy(row => row.Outcome)
                .Select(group => new RefusalCount(group.Key, group.Sum(row => row.Count)))
                .OrderByDescending(refusal => refusal.Count)
                .ToList());
    }

    private static int ClientsWithAccess(IEnumerable<Client> clients, Guid? inboundId, DateTimeOffset now) => clients
        .Where(client => inboundId is not { } id || client.Inbounds.Any(link => link.InboundId == id))
        .Count(client => ClientState.Of(client, now) == ClientStatus.Active);

    private static List<TRow> Busiest<TKey, TRow>(
        IEnumerable<CallGroup> groups, Func<CallGroup, TKey> key, Func<TKey, CallStats, TRow> row) => groups
        .OrderByDescending(group => group.Stats.Calls)
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .ThenBy(group => group.Sub, StringComparer.Ordinal)
        .Select(group => row(key(group), group.Stats))
        .ToList();

    private async Task<List<DeviceStats>> DevicesAsync(EventScope scope, CancellationToken ct)
    {
        var groups = await scope.CallsAsync(CallGrouping.Device, ct);
        var ids = groups.Select(group => Guid.Parse(group.Key!)).ToList();
        var devices = await db.Devices.AsNoTracking()
            .Include(device => device.Apps)
            .Where(device => ids.Contains(device.Id))
            .ToDictionaryAsync(device => device.Id, ct);

        return Busiest(groups, group => Guid.Parse(group.Key!), (id, stats) => new DeviceStats(
            id,
            devices.GetValueOrDefault(id)?.Name,
            devices.GetValueOrDefault(id)?.Apps.Select(app => app.AppName).Distinct().Order(StringComparer.CurrentCulture).ToList() ?? [],
            stats));
    }
}
