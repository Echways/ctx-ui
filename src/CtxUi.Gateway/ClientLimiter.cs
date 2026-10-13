using System.Threading.RateLimiting;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

public sealed class ClientLimiter : IAsyncDisposable
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<ClientAccess> _rate = PartitionedRateLimiter.Create<ClientAccess, Key>(client =>
        client.Limits.RatePerMinute is { } perMinute
            ? RateLimitPartition.GetTokenBucketLimiter(new Key(client.Id, perMinute), key => new TokenBucketRateLimiterOptions
            {
                TokenLimit = key.Limit,
                TokensPerPeriod = key.Limit,
                ReplenishmentPeriod = Minute,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter(new Key(client.Id, 0)));

    private readonly PartitionedRateLimiter<ClientAccess> _slots = PartitionedRateLimiter.Create<ClientAccess, Key>(client =>
        client.Limits.MaxConcurrency is { } atOnce
            ? RateLimitPartition.GetConcurrencyLimiter(new Key(client.Id, atOnce), key => new ConcurrencyLimiterOptions
            {
                PermitLimit = key.Limit,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter(new Key(client.Id, 0)));

    public RateLimitLease AcquireRate(ClientAccess client) => _rate.AttemptAcquire(client);

    public RateLimitLease AcquireSlot(ClientAccess client) => _slots.AttemptAcquire(client);

    public static int SecondsUntilNextRequest(ClientAccess client) =>
        client.Limits.RatePerMinute is { } perMinute ? (int)Math.Ceiling(Minute.TotalSeconds / perMinute) : 1;

    public async ValueTask DisposeAsync()
    {
        await _rate.DisposeAsync();
        await _slots.DisposeAsync();
    }

    private readonly record struct Key(Guid ClientId, int Limit);
}
