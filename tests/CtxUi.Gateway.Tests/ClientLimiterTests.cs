using System.Collections.Frozen;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway.Tests;

public class ClientLimiterTests : IAsyncDisposable
{
    private readonly ClientLimiter _limiter = new(Options.Create(new GatewayOptions { ConcurrencyWait = TimeSpan.FromMilliseconds(200) }));

    private ValueTask<RateLimitLease> SlotAsync(ClientAccess client) => _limiter.AcquireSlotAsync(client, CancellationToken.None);

    private static ClientAccess Client(ClientLimits limits, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Иванов", true, null, FrozenSet<Guid>.Empty, new Dictionary<string, string>()) { Limits = limits };

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();

    [Fact]
    public async Task Client_without_limits_is_never_held_back()
    {
        var client = Client(ClientLimits.None);

        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            Assert.True(_limiter.AcquireRate(client).IsAcquired);
            Assert.True((await SlotAsync(client)).IsAcquired);
        }
    }

    [Fact]
    public void Requests_over_the_rate_are_refused()
    {
        var client = Client(new ClientLimits(RatePerMinute: 3));

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.True(_limiter.AcquireRate(client).IsAcquired);

        Assert.False(_limiter.AcquireRate(client).IsAcquired);
        Assert.False(_limiter.AcquireRate(client).IsAcquired);
    }

    [Fact]
    public async Task Allowance_comes_back_gradually_not_once_a_minute()
    {
        var client = Client(new ClientLimits(RatePerMinute: 600));
        for (var attempt = 0; attempt < 600; attempt++)
            _limiter.AcquireRate(client);
        Assert.False(_limiter.AcquireRate(client).IsAcquired);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var returned = false;
        while (!returned && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            returned = _limiter.AcquireRate(client).IsAcquired;
        }

        Assert.True(returned);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(3, 20)]
    [InlineData(7, 9)]
    [InlineData(60, 1)]
    [InlineData(100_000, 1)]
    public void Wait_hint_is_the_time_one_request_takes_to_come_back(int perMinute, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, ClientLimiter.SecondsUntilNextRequest(Client(new ClientLimits(RatePerMinute: perMinute))));

    [Fact]
    public void Rate_is_counted_per_client()
    {
        var busy = Client(new ClientLimits(RatePerMinute: 1));
        var other = Client(new ClientLimits(RatePerMinute: 1));
        _limiter.AcquireRate(busy);

        Assert.False(_limiter.AcquireRate(busy).IsAcquired);
        Assert.True(_limiter.AcquireRate(other).IsAcquired);
    }

    [Fact]
    public async Task Changed_limit_applies_to_the_next_request()
    {
        var id = Guid.NewGuid();
        var strict = Client(new ClientLimits(RatePerMinute: 1, MaxConcurrency: 1), id);
        _limiter.AcquireRate(strict);
        using var held = await SlotAsync(strict);
        Assert.False(_limiter.AcquireRate(strict).IsAcquired);
        Assert.False((await SlotAsync(strict)).IsAcquired);

        var relaxed = Client(new ClientLimits(RatePerMinute: 5, MaxConcurrency: 2), id);
        var unlimited = Client(ClientLimits.None, id);

        Assert.True(_limiter.AcquireRate(relaxed).IsAcquired);
        Assert.True((await SlotAsync(relaxed)).IsAcquired);
        Assert.True(_limiter.AcquireRate(unlimited).IsAcquired);
        Assert.True((await SlotAsync(unlimited)).IsAcquired);
    }

    [Fact]
    public async Task Slot_is_free_again_when_the_request_that_held_it_ends()
    {
        var client = Client(new ClientLimits(MaxConcurrency: 2));
        var first = await SlotAsync(client);
        using var second = await SlotAsync(client);
        using var refused = await SlotAsync(client);

        first.Dispose();
        using var afterRelease = await SlotAsync(client);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(refused.IsAcquired);
        Assert.True(afterRelease.IsAcquired);
    }

    [Fact]
    public async Task Request_arriving_while_the_previous_one_is_finishing_waits_for_its_slot()
    {
        var client = Client(new ClientLimits(MaxConcurrency: 1));
        var finishing = await SlotAsync(client);

        var next = SlotAsync(client).AsTask();
        finishing.Dispose();
        using var served = await next;

        Assert.True(served.IsAcquired);
    }

    [Fact]
    public async Task Waiting_requests_beyond_the_limit_are_refused_at_once()
    {
        var client = Client(new ClientLimits(MaxConcurrency: 1));
        using var held = await SlotAsync(client);
        var waiting = SlotAsync(client).AsTask();

        using var excess = await SlotAsync(client);

        Assert.False(excess.IsAcquired);
        Assert.False(waiting.IsCompleted);
        using var timedOut = await waiting;
        Assert.False(timedOut.IsAcquired);
    }

    [Fact]
    public async Task Slots_are_counted_per_client()
    {
        var busy = Client(new ClientLimits(MaxConcurrency: 1));
        var other = Client(new ClientLimits(MaxConcurrency: 1));
        using var held = await SlotAsync(busy);

        Assert.False((await SlotAsync(busy)).IsAcquired);
        Assert.True((await SlotAsync(other)).IsAcquired);
    }
}
