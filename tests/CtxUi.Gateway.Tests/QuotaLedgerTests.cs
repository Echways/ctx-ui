using System.Collections.Frozen;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway.Tests;

public class QuotaLedgerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.NewGuid();

    private readonly QuotaLedger _ledger = new();

    private static ClientAccess Client(int? quota = 3, int? resetEveryDays = 30, DateTimeOffset? periodStartedAt = null, Guid? id = null) =>
        new(id ?? Id, "Иванов", true, null, FrozenSet<Guid>.Empty, new Dictionary<string, string>())
        {
            Limits = new ClientLimits(QuotaCalls: quota, ResetEveryDays: resetEveryDays),
            PeriodStartedAt = periodStartedAt ?? Start,
        };

    private async Task<IReadOnlyCollection<StoredUsage>> FlushedAsync()
    {
        IReadOnlyCollection<StoredUsage> saved = [];
        await _ledger.FlushAsync(usage =>
        {
            saved = usage;
            return Task.CompletedTask;
        });
        return saved;
    }

    [Fact]
    public void Calls_within_the_quota_are_accepted_and_the_next_one_is_refused()
    {
        var client = Client(quota: 3);

        for (var call = 0; call < 3; call++)
            Assert.True(_ledger.Charge(client, 1, Start).Accepted);
        var refused = _ledger.Charge(client, 1, Start.AddDays(1));

        Assert.Equal(new QuotaCharge(false, Start, 1, 3, Start.AddDays(30)), refused);
    }

    [Fact]
    public async Task Refused_call_is_not_counted()
    {
        var client = Client(quota: 1);
        _ledger.Charge(client, 1, Start);

        _ledger.Charge(client, 1, Start);
        _ledger.Charge(client, 1, Start);

        Assert.Equal(1, Assert.Single(await FlushedAsync()).Calls);
    }

    [Fact]
    public async Task Several_calls_are_charged_all_or_nothing()
    {
        var client = Client(quota: 5);
        _ledger.Charge(client, 3, Start);

        Assert.False(_ledger.Charge(client, 3, Start).Accepted);
        Assert.True(_ledger.Charge(client, 2, Start).Accepted);
        Assert.Equal(5, Assert.Single(await FlushedAsync()).Calls);
    }

    [Fact]
    public async Task Calls_are_counted_even_when_there_is_no_quota()
    {
        var client = Client(quota: null, resetEveryDays: null);

        for (var call = 0; call < 50; call++)
            Assert.True(_ledger.Charge(client, 1, Start.AddDays(call)).Accepted);

        Assert.Equal(new StoredUsage(Id, Start, 50), Assert.Single(await FlushedAsync()));
    }

    [Fact]
    public void Refund_gives_the_calls_back()
    {
        var client = Client(quota: 1);
        var charge = _ledger.Charge(client, 1, Start);
        Assert.False(_ledger.Charge(client, 1, Start).Accepted);

        _ledger.Refund(Id, charge);

        Assert.True(_ledger.Charge(client, 1, Start).Accepted);
    }

    [Fact]
    public async Task Refund_of_a_refused_charge_or_of_a_past_period_changes_nothing()
    {
        var client = Client(quota: 2, resetEveryDays: 7);
        var old = _ledger.Charge(client, 1, Start);
        _ledger.Charge(client, 2, Start.AddDays(7));
        var refused = _ledger.Charge(client, 1, Start.AddDays(7));

        _ledger.Refund(Id, old);
        _ledger.Refund(Id, refused);
        _ledger.Refund(Guid.NewGuid(), old);

        Assert.Equal(new StoredUsage(Id, Start.AddDays(7), 2), Assert.Single(await FlushedAsync()));
    }

    [Fact]
    public void Counter_starts_over_when_the_reset_interval_passes()
    {
        var client = Client(quota: 1, resetEveryDays: 30);
        _ledger.Charge(client, 1, Start);

        var stillOldPeriod = _ledger.Charge(client, 1, Start.AddDays(30).AddMilliseconds(-1));
        var newPeriod = _ledger.Charge(client, 1, Start.AddDays(30));

        Assert.False(stillOldPeriod.Accepted);
        Assert.Equal(new QuotaCharge(true, Start.AddDays(30), 1, 1, Start.AddDays(60)), newPeriod);
    }

    [Fact]
    public void Without_a_reset_interval_the_quota_is_for_good()
    {
        var client = Client(quota: 1, resetEveryDays: null);
        _ledger.Charge(client, 1, Start);

        var refused = _ledger.Charge(client, 1, Start.AddYears(3));

        Assert.False(refused.Accepted);
        Assert.Null(refused.ResetsAt);
    }

    [Fact]
    public void Period_restarted_by_the_administrator_clears_the_counter()
    {
        _ledger.Charge(Client(quota: 1), 1, Start);
        Assert.False(_ledger.Charge(Client(quota: 1), 1, Start.AddDays(2)).Accepted);

        var afterReset = _ledger.Charge(Client(quota: 1, periodStartedAt: Start.AddDays(2)), 1, Start.AddDays(2));

        Assert.True(afterReset.Accepted);
    }

    [Fact]
    public void Bigger_quota_keeps_what_is_already_used()
    {
        for (var call = 0; call < 3; call++)
            _ledger.Charge(Client(quota: 3), 1, Start);

        Assert.True(_ledger.Charge(Client(quota: 5), 1, Start).Accepted);
        Assert.True(_ledger.Charge(Client(quota: 5), 1, Start).Accepted);
        Assert.False(_ledger.Charge(Client(quota: 5), 1, Start).Accepted);
    }

    [Fact]
    public void Restored_counter_continues_from_the_saved_value()
    {
        _ledger.Restore([new StoredUsage(Id, Start, 2)]);

        Assert.True(_ledger.Charge(Client(quota: 3), 1, Start.AddDays(1)).Accepted);
        Assert.False(_ledger.Charge(Client(quota: 3), 1, Start.AddDays(1)).Accepted);
    }

    [Fact]
    public void Restored_counter_of_a_finished_period_does_not_count()
    {
        _ledger.Restore([new StoredUsage(Id, Start, 3)]);

        Assert.True(_ledger.Charge(Client(quota: 3), 3, Start.AddDays(30)).Accepted);
    }

    [Fact]
    public async Task Flush_saves_each_change_once()
    {
        var other = Guid.NewGuid();
        _ledger.Charge(Client(), 1, Start);
        _ledger.Charge(Client(id: other), 2, Start);

        var first = await FlushedAsync();
        var idle = await FlushedAsync();
        _ledger.Charge(Client(), 1, Start);
        var second = await FlushedAsync();

        Assert.Equal(
            [new StoredUsage(Id, Start, 1), new StoredUsage(other, Start, 2)],
            first.OrderBy(usage => usage.Calls));
        Assert.Empty(idle);
        Assert.Equal([new StoredUsage(Id, Start, 2)], second);
    }

    [Fact]
    public async Task Counters_that_failed_to_save_are_saved_by_the_next_flush()
    {
        _ledger.Charge(Client(), 1, Start);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _ledger.FlushAsync(_ => throw new InvalidOperationException("база занята")));

        Assert.Equal([new StoredUsage(Id, Start, 1)], await FlushedAsync());
    }

    [Fact]
    public void Parallel_calls_never_exceed_the_quota()
    {
        var client = Client(quota: 100);
        var accepted = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (_ledger.Charge(client, 1, Start).Accepted)
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(100, accepted);
    }
}
