using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;

namespace CtxUi.Gateway;

public sealed class ClientTouch(IServiceScopeFactory scopes)
{
    private readonly TouchThrottle _throttle = new(TimeSpan.FromMinutes(1));
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _pending = new();

    public void Touch(Guid clientId, DateTimeOffset now)
    {
        if (_throttle.ShouldTouch(clientId, now))
            _pending.AddOrUpdate(clientId, now, (_, seen) => seen > now ? seen : now);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var seen = _pending.ToArray();
        if (seen.Length == 0)
            return;

        await using (var scope = scopes.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ClientService>().MarkUsedAsync(seen, ct);

        foreach (var pair in seen)
            _pending.TryRemove(pair);
    }
}
