using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Tests;

public class OverviewServiceTests
{
    private static readonly Guid Stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private InboundService Inbounds(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private Task<Overview> LoadAsync(TestDatabase database)
    {
        var db = database.NewContext();
        return new OverviewService(db, new UsageStore(db), _clock).LoadAsync();
    }

    private static UsageRecord Call(
        Guid client,
        Guid inbound,
        DateTimeOffset at,
        UsageOutcome outcome = UsageOutcome.Ok,
        string? method = "tools/call") =>
        new(client, inbound, at, method, method == "tools/call" ? "echo" : null, 100, outcome);

    [Fact]
    public async Task Empty_installation_is_all_zeroes()
    {
        await using var database = await TestDatabase.CreateAsync();

        var overview = await LoadAsync(database);

        Assert.Equal((0, 0, 0), (overview.Clients, overview.ActiveClients, overview.SeenClients));
        Assert.Equal((0, 0, 0), (overview.Calls, overview.ToolCalls, overview.Failures));
        Assert.Empty(overview.Inbounds);
        Assert.Empty(overview.Days);
    }

    [Fact]
    public async Task Clients_are_counted_by_state_and_by_use_in_the_last_day()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Inbounds(database).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var active = await Clients(database).AddAsync("Активный", ["echo"]);
        var idle = await Clients(database).AddAsync("Давний", ["echo"]);
        await Clients(database).AddAsync("Срочный", ["echo"], TestData.Now.AddHours(1));
        await Clients(database).AddAsync("Отключённый", ["echo"]);
        await Clients(database).SetEnabledAsync("Отключённый", false);
        await Clients(database).MarkUsedAsync(active.Client.Id, TestData.Now.AddHours(-1));
        await Clients(database).MarkUsedAsync(idle.Client.Id, TestData.Now.AddHours(-25));
        _clock.Advance(TimeSpan.FromHours(2));

        var overview = await LoadAsync(database);

        Assert.Equal(4, overview.Clients);
        Assert.Equal(2, overview.ActiveClients);
        Assert.Equal(1, overview.SeenClients);
    }

    [Fact]
    public async Task Calls_of_the_last_twenty_four_hours_are_counted_per_inbound()
    {
        await using var database = await TestDatabase.CreateAsync();
        var echo = await Inbounds(database).AddHttpAsync("echo", "Эхо", "https://mcp.example.org/mcp");
        var docs = await Inbounds(database).AddHttpAsync("docs", null, "https://docs.example.org/mcp");
        var idle = await Inbounds(database).AddHttpAsync("idle", null, "https://idle.example.org/mcp");
        await Inbounds(database).SetEnabledAsync("idle", false);
        var issued = await Clients(database).AddAsync("Иванов", ["echo", "docs"]);
        var who = issued.Client.Id;
        await new UsageStore(database.NewContext()).AppendAsync(
        [
            Call(who, echo.Id, TestData.Now.AddHours(-23)),
            Call(who, echo.Id, TestData.Now.AddHours(-1), UsageOutcome.UpstreamFailed),
            Call(who, echo.Id, TestData.Now.AddHours(-1), UsageOutcome.UpstreamRejected),
            Call(who, echo.Id, TestData.Now.AddHours(-1), UsageOutcome.QuotaExhausted),
            Call(who, echo.Id, TestData.Now.AddHours(-25)),
            Call(who, docs.Id, TestData.Now.AddMinutes(-5), method: "tools/list"),
            Call(who, docs.Id, TestData.Now.AddMinutes(-5), UsageOutcome.ClientAborted),
        ]);

        var overview = await LoadAsync(database);

        Assert.Equal((5, 4, 1), (overview.Calls, overview.ToolCalls, overview.Failures));
        Assert.Equal(
        [
            new InboundActivity(docs.Id, "docs", "docs", true, 1, 2, 0),
            new InboundActivity(echo.Id, "echo", "Эхо", true, 1, 3, 1),
            new InboundActivity(idle.Id, "idle", "idle", false, 0, 0, 0),
        ],
        overview.Inbounds);
    }

    [Fact]
    public async Task Calls_to_an_inbound_removed_since_stay_in_the_totals()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new UsageStore(database.NewContext()).AppendAsync([Call(Stranger, Stranger, TestData.Now.AddHours(-1))]);

        var overview = await LoadAsync(database);

        Assert.Equal(1, overview.Calls);
        Assert.Empty(overview.Inbounds);
    }

    [Fact]
    public async Task Daily_totals_cover_seven_days_ending_today()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new UsageStore(database.NewContext()).AppendAsync(
        [
            Call(Stranger, Stranger, TestData.Now),
            Call(Stranger, Stranger, TestData.Now, UsageOutcome.QuotaExhausted),
            Call(Stranger, Stranger, TestData.Now.AddDays(-6)),
            Call(Stranger, Stranger, TestData.Now.AddDays(-7)),
        ]);

        var overview = await LoadAsync(database);

        Assert.Equal([new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 12)], overview.Days.Select(day => day.Day));
        Assert.Equal((1, 1), (overview.Days[1].Calls, overview.Days[1].Refused));
    }

    [Fact]
    public async Task Day_is_cut_into_ninety_six_quarters_ending_with_the_current_one()
    {
        await using var database = await TestDatabase.CreateAsync();
        _clock.Advance(TimeSpan.FromMinutes(7));

        var quarters = (await LoadAsync(database)).Quarters;

        Assert.Equal(96, quarters.Count);
        Assert.Equal(TestData.Now.AddMinutes(-95 * 15), quarters[0].Start);
        Assert.Equal(TestData.Now, quarters[^1].Start);
        Assert.All(quarters.Zip(quarters.Skip(1)), pair => Assert.Equal(TimeSpan.FromMinutes(15), pair.Second.Start - pair.First.Start));
        Assert.All(quarters, quarter => Assert.Equal((0, 0), (quarter.Calls, quarter.Failures)));
    }

    [Fact]
    public async Task Calls_fall_into_the_quarter_they_were_made_in()
    {
        await using var database = await TestDatabase.CreateAsync();
        _clock.Advance(TimeSpan.FromMinutes(7));
        var first = TestData.Now.AddMinutes(-95 * 15);
        await new UsageStore(database.NewContext()).AppendAsync(
        [
            Call(Stranger, Stranger, first.AddMilliseconds(-1)),
            Call(Stranger, Stranger, first),
            Call(Stranger, Stranger, first.AddMinutes(15).AddMilliseconds(-1), UsageOutcome.UpstreamFailed),
            Call(Stranger, Stranger, first.AddMinutes(15)),
            Call(Stranger, Stranger, TestData.Now.AddMinutes(-1), UsageOutcome.QuotaExhausted),
            Call(Stranger, Stranger, TestData.Now.AddMinutes(7)),
            Call(Stranger, Stranger, TestData.Now.AddMinutes(7), UsageOutcome.UpstreamFailed),
        ]);

        var quarters = (await LoadAsync(database)).Quarters;

        Assert.Equal((2, 1), (quarters[0].Calls, quarters[0].Failures));
        Assert.Equal((1, 0), (quarters[1].Calls, quarters[1].Failures));
        Assert.Equal((0, 0), (quarters[94].Calls, quarters[94].Failures));
        Assert.Equal((2, 1), (quarters[^1].Calls, quarters[^1].Failures));
        Assert.Equal(5, quarters.Sum(quarter => quarter.Calls));
    }
}
