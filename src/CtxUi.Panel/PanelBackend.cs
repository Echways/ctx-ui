using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Routing;

namespace CtxUi.Panel;

public sealed class PanelBackend(IServiceScopeFactory scopes, IRoutingRefresh routing)
{
    public async Task<T> ReadAsync<T>(Func<IServiceProvider, Task<T>> read)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await read(scope.ServiceProvider);
    }

    public async Task<T> ChangeAsync<T>(Func<IServiceProvider, Task<T>> change)
    {
        var result = await ReadAsync(change);
        await routing.RefreshAsync();
        return result;
    }

    public Task ChangeAsync(Func<IServiceProvider, Task> change) => ChangeAsync(async services =>
    {
        await change(services);
        return true;
    });
}
