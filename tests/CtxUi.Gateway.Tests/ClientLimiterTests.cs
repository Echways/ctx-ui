using System.Collections.Frozen;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway.Tests;

public class ClientLimiterTests : IAsyncDisposable
{
    private readonly ClientLimiter _limiter = new();

    private static ClientAccess Client(ClientLimits limits, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Иванов", true, null, FrozenSet<Guid>.Empty, new Dictionary<string, string>()) { Limits = limits };

    public ValueTask DisposeAsync() => _limiter.DisposeAsync();

    [Fact]
    public void Client_without_limits_is_never_held_back()
    {
        var client = Client(ClientLimits.None);

        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            Assert.True(_limiter.AcquireRate(client).IsAcquired);
            Assert.True(_limiter.AcquireSlot(client).IsAcquired);
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
    public void Changed_limit_applies_to_the_next_request()
    {
        var id = Guid.NewGuid();
        var strict = Client(new ClientLimits(RatePerMinute: 1, MaxConcurrency: 1), id);
        _limiter.AcquireRate(strict);
        using var held = _limiter.AcquireSlot(strict);
        Assert.False(_limiter.AcquireRate(strict).IsAcquired);
        Assert.False(_limiter.AcquireSlot(strict).IsAcquired);

        var relaxed = Client(new ClientLimits(RatePerMinute: 5, MaxConcurrency: 2), id);
        var unlimited = Client(ClientLimits.None, id);

        Assert.True(_limiter.AcquireRate(relaxed).IsAcquired);
        Assert.True(_limiter.AcquireSlot(relaxed).IsAcquired);
        Assert.True(_limiter.AcquireRate(unlimited).IsAcquired);
        Assert.True(_limiter.AcquireSlot(unlimited).IsAcquired);
    }

    [Fact]
    public void Slot_is_free_again_when_the_request_that_held_it_ends()
    {
        var client = Client(new ClientLimits(MaxConcurrency: 2));
        var first = _limiter.AcquireSlot(client);
        using var second = _limiter.AcquireSlot(client);
        using var refused = _limiter.AcquireSlot(client);

        first.Dispose();
        using var afterRelease = _limiter.AcquireSlot(client);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(refused.IsAcquired);
        Assert.True(afterRelease.IsAcquired);
    }

    [Fact]
    public void Slots_are_counted_per_client()
    {
        var busy = Client(new ClientLimits(MaxConcurrency: 1));
        var other = Client(new ClientLimits(MaxConcurrency: 1));
        using var held = _limiter.AcquireSlot(busy);

        Assert.False(_limiter.AcquireSlot(busy).IsAcquired);
        Assert.True(_limiter.AcquireSlot(other).IsAcquired);
    }
}
