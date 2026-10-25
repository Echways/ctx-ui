using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;

namespace CtxUi.Core.Tests;

public class ClientQuotaTests
{
    private static readonly ClientLimits Limits = new(RatePerMinute: 60, MaxConcurrency: 2, QuotaCalls: 100, ResetEveryDays: 30);

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private ClientService Clients(TestDatabase database) => new(database.NewContext(), _vault, _clock);

    private async Task<TestDatabase> DatabaseWithEchoAsync()
    {
        var database = await TestDatabase.CreateAsync();
        await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        return database;
    }

    private static Task<long> RevisionAsync(TestDatabase database) =>
        database.NewContext().RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    [Fact]
    public async Task Limits_given_at_issue_are_saved_and_the_period_starts_now()
    {
        await using var database = await DatabaseWithEchoAsync();

        await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);

        var client = await Clients(database).GetAsync("Иванов");
        Assert.Equal(Limits, ClientLimits.Of(client));
        Assert.Equal(TestData.Now, client.PeriodStartedAt);
        Assert.Equal(0, client.UsedCalls);
        Assert.Null(client.UsedSince);
    }

    [Fact]
    public async Task Client_issued_without_limits_has_none()
    {
        await using var database = await DatabaseWithEchoAsync();

        await Clients(database).AddAsync("Иванов", ["echo"]);

        Assert.Equal(ClientLimits.None, ClientLimits.Of(await Clients(database).GetAsync("Иванов")));
    }

    [Fact]
    public async Task Limit_outside_the_range_is_rejected_at_issue_and_on_update()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);

        await Assert.ThrowsAsync<DomainException>(() =>
            Clients(database).AddAsync("Петров", ["echo"], limits: new ClientLimits(QuotaCalls: 0)));
        await Assert.ThrowsAsync<DomainException>(() =>
            Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Limits = new ClientLimits(RatePerMinute: -1) }));

        Assert.Single(await Clients(database).ListAsync());
        Assert.Equal(Limits, ClientLimits.Of(await Clients(database).GetAsync("Иванов")));
    }

    [Fact]
    public async Task Update_that_does_not_mention_limits_keeps_them()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { ChangeExpiry = true, ExpiresAt = TestData.Now.AddDays(5) });

        Assert.Equal(Limits, ClientLimits.Of(await Clients(database).GetAsync("Иванов")));
    }

    [Fact]
    public async Task Empty_limits_remove_every_restriction()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Limits = ClientLimits.None });

        Assert.Equal(ClientLimits.None, ClientLimits.Of(await Clients(database).GetAsync("Иванов")));
    }

    [Fact]
    public async Task Changing_the_quota_size_keeps_the_period()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);
        _clock.Advance(TimeSpan.FromDays(3));

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Limits = Limits with { QuotaCalls = 500, RatePerMinute = null } });

        var client = await Clients(database).GetAsync("Иванов");
        Assert.Equal(500, client.QuotaCalls);
        Assert.Null(client.RatePerMinute);
        Assert.Equal(TestData.Now, client.PeriodStartedAt);
    }

    [Fact]
    public async Task First_quota_of_a_client_starts_a_new_period()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"]);
        _clock.Advance(TimeSpan.FromDays(60));

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Limits = new ClientLimits(QuotaCalls: 100) });

        Assert.Equal(TestData.Now.AddDays(60), (await Clients(database).GetAsync("Иванов")).PeriodStartedAt);
    }

    [Fact]
    public async Task Changing_the_reset_interval_starts_a_new_period()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);
        _clock.Advance(TimeSpan.FromDays(3));

        await Clients(database).UpdateAsync(issued.Client.Id, new ClientUpdate { Limits = Limits with { ResetEveryDays = 7 } });

        Assert.Equal(TestData.Now.AddDays(3), (await Clients(database).GetAsync("Иванов")).PeriodStartedAt);
    }

    [Fact]
    public async Task Reset_starts_a_new_period_now()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);
        _clock.Advance(TimeSpan.FromDays(3));

        await Clients(database).ResetQuotaAsync(issued.Client.Id);

        Assert.Equal(TestData.Now.AddDays(3), (await Clients(database).GetAsync("Иванов")).PeriodStartedAt);
        await Assert.ThrowsAsync<NotFoundException>(() => Clients(database).ResetQuotaAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Saved_counters_are_read_back_and_do_not_bump_the_routing_revision()
    {
        await using var database = await DatabaseWithEchoAsync();
        var counted = (await Clients(database).AddAsync("Иванов", ["echo"])).Client;
        await Clients(database).AddAsync("Петров", ["echo"]);
        var revision = await RevisionAsync(database);

        await Clients(database).SaveUsageAsync(
        [
            new StoredUsage(counted.Id, TestData.Now, 57),
            new StoredUsage(Guid.NewGuid(), TestData.Now, 1),
        ]);

        var stored = Assert.Single(await Clients(database).LoadUsageAsync());
        Assert.Equal(new StoredUsage(counted.Id, TestData.Now, 57), stored);
        Assert.Equal(57, QuotaPeriod.UsedCalls(await Clients(database).GetAsync("Иванов"), TestData.Now));
        Assert.Equal(revision, await RevisionAsync(database));
    }

    [Fact]
    public async Task Snapshot_carries_limits_period_and_the_body_limit()
    {
        await using var database = await DatabaseWithEchoAsync();
        var issued = await Clients(database).AddAsync("Иванов", ["echo"], limits: Limits);
        var loader = new RoutingSnapshotLoader(database.NewContext(), _vault, _clock, NullLogger<RoutingSnapshotLoader>.Instance);

        var decision = (await loader.LoadAsync()).Authorize(Credential.FromKey(issued.Token), "echo", TestData.Now);

        Assert.Equal(Limits, decision.Client!.Limits);
        Assert.Equal(TestData.Now, decision.Client.PeriodStartedAt);
        Assert.Equal(Inbound.DefaultMaxBodyBytes, decision.Route!.MaxBodyBytes);
    }
}
