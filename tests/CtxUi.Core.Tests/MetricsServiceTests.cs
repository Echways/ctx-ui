using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Metrics;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Tests;

public class MetricsServiceTests
{
    private static readonly Guid Stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private MetricsService Metrics(TestDatabase database, int retentionDays = 30) =>
        new(database.NewContext(), Options.Create(new UsageOptions { UsageRetentionDays = retentionDays }), _clock);

    private Task<MetricsReport> LoadAsync(TestDatabase database, MetricsFilter? filter = null, int? days = null) =>
        Metrics(database).LoadAsync(filter ?? MetricsFilter.All, days);

    private static Task AppendAsync(TestDatabase database, params UsageRecord[] records) =>
        new UsageStore(database.NewContext()).AppendAsync(records);

    private static UsageRecord Call(
        Guid client,
        Guid inbound,
        string? method = "tools/call",
        string? tool = "echo",
        UsageOutcome outcome = UsageOutcome.Ok,
        int durationMs = 100,
        DateTimeOffset? at = null,
        int status = 200,
        Guid? device = null) =>
        new(client, inbound, at ?? TestData.Now.AddMinutes(-5), method, method == "tools/call" ? tool : null, durationMs, outcome, device)
        {
            Status = status,
            RequestBytes = 10,
            ResponseBytes = 1_000,
        };

    private async Task<(Guid Echo, Guid Docs, Guid Ivanov, Guid Petrov)> SeedAsync(TestDatabase database)
    {
        var inbounds = new InboundService(database.NewContext(), _vault, _clock);
        var echo = await inbounds.AddHttpAsync("echo", "Эхо", "https://mcp.example.org/echo");
        var docs = await inbounds.AddHttpAsync("docs", null, "https://mcp.example.org/docs");
        var clients = new ClientService(database.NewContext(), _vault, _clock);
        var ivanov = await clients.AddAsync("Иванов", ["echo", "docs"]);
        var petrov = await clients.AddAsync("Петров", ["echo"]);
        return (echo.Id, docs.Id, ivanov.Client.Id, petrov.Client.Id);
    }

    [Fact]
    public async Task Empty_installation_reports_zeroes_over_the_whole_period()
    {
        await using var database = await TestDatabase.CreateAsync();

        var report = await LoadAsync(database);

        Assert.Equal(7, report.Period.Days);
        Assert.Equal(CallStats.None, report.Totals);
        Assert.Equal(new Funnel(0, 0, 0, 0, 0), report.Funnel);
        Assert.Equal(84, report.Slots.Count);
        Assert.All(report.Slots, slot => Assert.Equal((0, 0), (slot.Calls, slot.Failures)));
        Assert.Equal(9, report.Latency.Count);
        Assert.All(report.Latency, bucket => Assert.Equal(0, bucket.Calls));
        Assert.Empty(report.Inbounds);
        Assert.Empty(report.Errors);
        Assert.Empty(report.Refusals);
        Assert.Equal(0, report.ClientsWithAccess);
    }

    [Fact]
    public async Task Totals_count_outcomes_traffic_and_exact_percentiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, _, ivanov, petrov) = await SeedAsync(database);
        await AppendAsync(database, Enumerable.Range(1, 100).Select(ms => Call(ivanov, echo, durationMs: ms)).ToArray());
        await AppendAsync(
            database,
            Call(petrov, echo, outcome: UsageOutcome.UpstreamFailed, status: 502, durationMs: 1),
            Call(petrov, echo, outcome: UsageOutcome.UpstreamRejected, status: 404, durationMs: 1),
            Call(petrov, echo, method: "tools/list", outcome: UsageOutcome.ClientAborted, durationMs: 1),
            Call(petrov, echo, outcome: UsageOutcome.QuotaExhausted, durationMs: 0));

        var totals = (await LoadAsync(database)).Totals;

        Assert.Equal((103, 102, 1, 1, 1, 2), (totals.Calls, totals.ToolCalls, totals.Failures, totals.Rejected, totals.Aborted, totals.Clients));
        Assert.Equal((1_030L, 103_000L), (totals.RequestBytes, totals.ResponseBytes));
        Assert.Equal(new Latency(49, 95, 99, 100), totals.Latency);
        Assert.Equal(2, totals.Errors);
        Assert.Equal(TestData.Now.AddMinutes(-5), totals.LastAt);
    }

    [Fact]
    public async Task Calls_outside_the_period_are_left_out_and_the_timeline_places_the_rest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, _, ivanov, _) = await SeedAsync(database);
        await AppendAsync(
            database,
            Call(ivanov, echo, at: TestData.Now.AddDays(-2)),
            Call(ivanov, echo, at: TestData.Now.AddHours(-3), outcome: UsageOutcome.UpstreamFailed),
            Call(ivanov, echo, at: TestData.Now.AddMinutes(-1)));

        var report = await LoadAsync(database, days: 1);

        Assert.Equal(2, report.Totals.Calls);
        Assert.Equal(96, report.Slots.Count);
        Assert.Equal(TestData.Now.AddMinutes(-15 * 95), report.From);
        Assert.Equal((1, 0), (report.Slots[^2].Calls, report.Slots[^2].Failures));
        var failed = Assert.Single(report.Slots, slot => slot.Failures > 0);
        Assert.Equal(TestData.Now.AddHours(-3), failed.Start);
    }

    [Fact]
    public async Task Breakdowns_are_sorted_by_calls_and_carry_names()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, docs, ivanov, petrov) = await SeedAsync(database);
        await AppendAsync(
            database,
            Call(ivanov, echo, tool: "search"),
            Call(ivanov, echo, tool: "search"),
            Call(ivanov, echo, tool: "search"),
            Call(ivanov, docs, tool: "read"),
            Call(ivanov, docs, tool: "read"),
            Call(petrov, echo, method: "initialize"),
            Call(petrov, echo, method: "tools/list"),
            Call(Stranger, Stranger, tool: "lost"));

        var report = await LoadAsync(database);

        Assert.Equal(
            [("echo", "Эхо", 5), ("docs", "docs", 2), (null, null, 1)],
            report.Inbounds.Select(row => (row.Slug, row.Name, row.Stats.Calls)));
        Assert.Equal([("echo", "search", 3), ("docs", "read", 2), (null, "lost", 1)], report.Tools.Select(row => (row.Slug, row.Tool, row.Stats.Calls)));
        Assert.Equal([("tools/call", 6), ("initialize", 1), ("tools/list", 1)], report.Methods.Select(row => (row.Method, row.Stats.Calls)));
        Assert.Equal([("Иванов", 5), ("Петров", 2), (null, 1)], report.Clients.Select(row => (row.Name, row.Stats.Calls)));
        Assert.Empty(report.Devices);
    }

    [Fact]
    public async Task Client_filter_narrows_everything_and_lists_the_devices()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, docs, ivanov, petrov) = await SeedAsync(database);
        var device = Guid.NewGuid();
        await AppendAsync(
            database,
            Call(ivanov, echo, device: device),
            Call(ivanov, echo),
            Call(ivanov, docs),
            Call(petrov, echo),
            Call(ivanov, echo, method: null, outcome: UsageOutcome.RateLimited),
            Call(petrov, echo, method: null, outcome: UsageOutcome.RateLimited));

        var report = await LoadAsync(database, new MetricsFilter(ClientId: ivanov));

        Assert.Equal(new MetricsSubject("Иванов", null, null), report.Subject);
        Assert.Equal(3, report.Totals.Calls);
        Assert.Null(report.ClientsWithAccess);
        Assert.Empty(report.Clients);
        Assert.Equal(["echo", "docs"], report.Inbounds.Select(row => row.Slug));
        var used = Assert.Single(report.Devices);
        Assert.Equal((device, (string?)null, 1), (used.Id, used.Name, used.Stats.Calls));
        Assert.Equal([new RefusalCount(UsageOutcome.RateLimited, 1)], report.Refusals);
    }

    [Fact]
    public async Task Inbound_and_tool_filter_keep_only_that_tool()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, docs, ivanov, petrov) = await SeedAsync(database);
        await AppendAsync(
            database,
            Call(ivanov, echo, tool: "search"),
            Call(petrov, echo, tool: "search", outcome: UsageOutcome.QuotaExhausted),
            Call(ivanov, echo, tool: "fetch"),
            Call(ivanov, docs, tool: "search"),
            Call(ivanov, echo, method: "tools/list"),
            Call(ivanov, echo, method: null, outcome: UsageOutcome.BodyTooLarge));

        var report = await LoadAsync(database, new MetricsFilter(InboundId: echo, Tool: "search"));

        Assert.Equal(new MetricsSubject(null, "echo", "search"), report.Subject);
        Assert.Equal(1, report.Totals.Calls);
        Assert.Empty(report.Inbounds);
        Assert.Empty(report.Tools);
        Assert.Empty(report.Methods);
        Assert.Equal(["Иванов"], report.Clients.Select(row => row.Name));
        Assert.Equal([new RefusalCount(UsageOutcome.QuotaExhausted, 1)], report.Refusals);
        Assert.Equal(2, report.ClientsWithAccess);
    }

    [Fact]
    public async Task Funnel_follows_clients_from_the_first_call_to_a_successful_tool()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, _, ivanov, petrov) = await SeedAsync(database);
        await AppendAsync(
            database,
            Call(ivanov, echo, method: "initialize"),
            Call(ivanov, echo, method: "tools/list"),
            Call(ivanov, echo),
            Call(petrov, echo, method: "initialize"),
            Call(petrov, echo, outcome: UsageOutcome.UpstreamFailed));

        var report = await LoadAsync(database);

        Assert.Equal(new Funnel(2, 2, 1, 2, 1), report.Funnel);
        Assert.Equal(2, report.ClientsWithAccess);
    }

    [Fact]
    public async Task Errors_are_grouped_by_place_and_status_most_frequent_first()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, docs, ivanov, _) = await SeedAsync(database);
        await AppendAsync(
            database,
            Call(ivanov, echo, outcome: UsageOutcome.UpstreamFailed, status: 504, at: TestData.Now.AddMinutes(-9)),
            Call(ivanov, echo, outcome: UsageOutcome.UpstreamFailed, status: 504, at: TestData.Now.AddMinutes(-2)),
            Call(ivanov, echo, outcome: UsageOutcome.UpstreamFailed, status: 502),
            Call(ivanov, docs, method: "tools/list", outcome: UsageOutcome.UpstreamRejected, status: 404),
            Call(ivanov, docs));

        var errors = (await LoadAsync(database)).Errors;

        Assert.Equal(3, errors.Count);
        Assert.Equal(new ErrorGroup(echo, "echo", "tools/call", "echo", UsageOutcome.UpstreamFailed, 504, 2, TestData.Now.AddMinutes(-2)), errors[0]);
        Assert.Contains(errors, error => error is { Slug: "docs", Method: "tools/list", Tool: null, Status: 404, Count: 1 });
    }

    [Fact]
    public async Task Latency_buckets_split_calls_by_duration()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (echo, _, ivanov, _) = await SeedAsync(database);
        await AppendAsync(
            database,
            Call(ivanov, echo, durationMs: 99),
            Call(ivanov, echo, durationMs: 100),
            Call(ivanov, echo, durationMs: 40_000));

        var latency = (await LoadAsync(database)).Latency;

        Assert.Equal(new LatencyBucket(0, 100, 1), latency[0]);
        Assert.Equal(new LatencyBucket(100, 250, 1), latency[1]);
        Assert.Equal(new LatencyBucket(30_000, null, 1), latency[^1]);
    }

    [Fact]
    public async Task Periods_never_reach_past_the_log_retention()
    {
        await using var database = await TestDatabase.CreateAsync();

        Assert.Equal([1, 7, 30], Metrics(database).Periods.Select(period => period.Days));
        Assert.Equal([1, 7, 30, 90], Metrics(database, retentionDays: 365).Periods.Select(period => period.Days));
        Assert.Equal([1], Metrics(database, retentionDays: 3).Periods.Select(period => period.Days));
        Assert.Equal(1, Metrics(database, retentionDays: 3).Resolve(null).Days);
        var refused = await Assert.ThrowsAsync<DomainException>(() => LoadAsync(database, days: 90));
        Assert.Contains("1, 7, 30", refused.Message);
    }
}
