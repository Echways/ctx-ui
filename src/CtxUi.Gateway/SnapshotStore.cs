using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

public sealed class SnapshotStore(IServiceScopeFactory scopes) : IRoutingRefresh, ISnapshotSource, IDisposable
{
    private readonly SemaphoreSlim _reloading = new(1, 1);
    private volatile RoutingSnapshot _current = RoutingSnapshot.Empty;

    public RoutingSnapshot Current => _current;

    public Task RefreshAsync(CancellationToken ct = default) => ReloadAsync(ct);

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reloading.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            _current = await scope.ServiceProvider.GetRequiredService<RoutingSnapshotLoader>().LoadAsync(ct);
        }
        finally
        {
            _reloading.Release();
        }
    }

    public async Task<bool> ReloadIfChangedAsync(CancellationToken ct = default)
    {
        long revision;
        await using (var scope = scopes.CreateAsyncScope())
            revision = await scope.ServiceProvider.GetRequiredService<RoutingSnapshotLoader>().CurrentRevisionAsync(ct);

        if (revision == _current.Revision)
            return false;

        await ReloadAsync(ct);
        return true;
    }

    public void Dispose() => _reloading.Dispose();
}
