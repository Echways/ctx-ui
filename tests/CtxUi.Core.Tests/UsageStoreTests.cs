using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Tests;

public class UsageStoreTests
{
    private static readonly Guid Ivanov = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Petrov = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Echo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Docs = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateOnly Today = new(2026, 10, 12);

    private static UsageStore Store(TestDatabase database) => new(database.NewContext());

    private static UsageRecord Call(
        UsageOutcome outcome = UsageOutcome.Ok,
        string? method = "tools/call",
        string? tool = "echo",
        int durationMs = 100,
        DateTimeOffset? at = null,
        Guid? client = null,
        Guid? inbound = null) =>
        new(client ?? Ivanov, inbound ?? Echo, at ?? TestData.Now, method, tool, durationMs, outcome);

    private static async Task<UsageDaily> SingleDayAsync(TestDatabase database) =>
        await database.NewContext().UsageDays.AsNoTracking().SingleAsync();

    [Fact]
    public async Task Forwarded_call_is_written_to_the_log_and_to_the_daily_row()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Store(database).AppendAsync([Call(durationMs: 120)]);

        var usage = Assert.Single(await Store(database).RecentAsync(null, 10));
        Assert.Equal(Ivanov, usage.ClientId);
        Assert.Equal(Echo, usage.InboundId);
        Assert.Equal(TestData.Now, usage.At);
        Assert.Equal("tools/call", usage.Method);
        Assert.Equal("echo", usage.Tool);
        Assert.Equal(120, usage.DurationMs);
        Assert.Equal(UsageOutcome.Ok, usage.Outcome);
        var daily = await SingleDayAsync(database);
        Assert.Equal(Today, daily.Day);
        Assert.Equal((1, 1, 0, 0, 120L), (daily.Calls, daily.ToolCalls, daily.Errors, daily.Refused, daily.DurationMs));
    }

    [Fact]
    public async Task Batches_of_the_same_day_client_and_inbound_add_up_in_one_row()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Store(database).AppendAsync([Call(), Call(method: "tools/list", tool: null)]);
        await Store(database).AppendAsync([Call(UsageOutcome.UpstreamFailed), Call(UsageOutcome.UpstreamRejected), Call(UsageOutcome.ClientAborted)]);

        var daily = await SingleDayAsync(database);
        Assert.Equal((5, 4, 1, 0, 500L), (daily.Calls, daily.ToolCalls, daily.Errors, daily.Refused, daily.DurationMs));
        Assert.Equal(5, (await Store(database).RecentAsync(null, 10)).Count);
    }

    [Fact]
    public async Task Refusal_by_rate_or_concurrency_is_counted_but_leaves_no_log_line()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Store(database).AppendAsync(
        [
            Call(UsageOutcome.RateLimited, method: null, tool: null, durationMs: 0),
            Call(UsageOutcome.ConcurrencyLimited, method: null, tool: null, durationMs: 0),
        ]);

        Assert.Empty(await Store(database).RecentAsync(null, 10));
        var daily = await SingleDayAsync(database);
        Assert.Equal((0, 0, 0, 2, 0L), (daily.Calls, daily.ToolCalls, daily.Errors, daily.Refused, daily.DurationMs));
    }

    [Fact]
    public async Task Quota_refusal_is_logged_and_counted_as_refused_not_as_a_call()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Store(database).AppendAsync([Call(UsageOutcome.QuotaExhausted, durationMs: 7)]);

        Assert.Equal(UsageOutcome.QuotaExhausted, Assert.Single(await Store(database).RecentAsync(null, 10)).Outcome);
        var daily = await SingleDayAsync(database);
        Assert.Equal((0, 0, 0, 1, 0L), (daily.Calls, daily.ToolCalls, daily.Errors, daily.Refused, daily.DurationMs));
    }

    [Fact]
    public async Task Day_is_taken_in_utc_and_rows_are_split_by_day_client_and_inbound()
    {
        await using var database = await TestDatabase.CreateAsync();
        var lastSecond = new DateTimeOffset(2026, 10, 12, 23, 59, 59, TimeSpan.Zero);
        var sameInstantInMoscow = new DateTimeOffset(2026, 10, 13, 2, 59, 59, TimeSpan.FromHours(3));

        await Store(database).AppendAsync(
        [
            Call(at: lastSecond),
            Call(at: sameInstantInMoscow),
            Call(at: lastSecond.AddSeconds(1)),
            Call(at: lastSecond, client: Petrov),
            Call(at: lastSecond, inbound: Docs),
        ]);

        var rows = await database.NewContext().UsageDays.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Single(row => row.Day == Today && row.ClientId == Ivanov && row.InboundId == Echo).Calls);
        Assert.Equal(1, rows.Single(row => row.Day == Today.AddDays(1)).Calls);
    }

    [Fact]
    public async Task Daily_totals_add_up_clients_and_inbounds_and_can_be_narrowed_to_one_client()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Store(database).AppendAsync(
        [
            Call(at: TestData.Now.AddDays(-2)),
            Call(at: TestData.Now.AddDays(-1)),
            Call(at: TestData.Now.AddDays(-1), client: Petrov, inbound: Docs, durationMs: 300),
            Call(UsageOutcome.RateLimited, method: null, at: TestData.Now, client: Petrov),
        ]);

        var everyone = await Store(database).DailyAsync(null, Today.AddDays(-1));
        var petrov = await Store(database).DailyAsync(Petrov, Today.AddDays(-7));

        Assert.Equal([Today.AddDays(-1), Today], everyone.Select(total => total.Day));
        Assert.Equal(new DailyTotal(Today.AddDays(-1), 2, 2, 0, 0, 400), everyone[0]);
        Assert.Equal(new DailyTotal(Today, 0, 0, 0, 1, 0), everyone[1]);
        Assert.Equal([new DailyTotal(Today.AddDays(-1), 1, 1, 0, 0, 300), new DailyTotal(Today, 0, 0, 0, 1, 0)], petrov);
    }

    [Fact]
    public async Task Recent_lines_come_newest_first_limited_and_narrowed_to_one_client()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Store(database).AppendAsync(
        [
            Call(tool: "first"),
            Call(tool: "second", client: Petrov),
            Call(tool: "third"),
        ]);

        var lastTwo = await Store(database).RecentAsync(null, 2);
        var ivanov = await Store(database).RecentAsync(Ivanov, 10);

        Assert.Equal(["third", "second"], lastTwo.Select(usage => usage.Tool));
        Assert.Equal(["third", "first"], ivanov.Select(usage => usage.Tool));
    }

    [Fact]
    public async Task Pruning_removes_old_log_lines_and_keeps_daily_rows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Store(database).AppendAsync(
        [
            Call(tool: "old", at: TestData.Now.AddDays(-31)),
            Call(tool: "edge", at: TestData.Now.AddDays(-30)),
            Call(tool: "fresh", at: TestData.Now),
        ]);

        var removed = await Store(database).PruneAsync(TestData.Now.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.Equal(["fresh", "edge"], (await Store(database).RecentAsync(null, 10)).Select(usage => usage.Tool));
        Assert.Equal(3, (await Store(database).DailyAsync(null, Today.AddDays(-40))).Count);
    }

    [Fact]
    public async Task Writing_usage_leaves_the_routing_revision_alone_and_an_empty_batch_is_a_no_op()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Store(database).AppendAsync([]);
        await Store(database).AppendAsync([Call()]);

        Assert.Equal(0, await database.NewContext().RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync());
    }
}
