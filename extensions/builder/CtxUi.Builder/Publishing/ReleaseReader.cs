using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Publishing;

public sealed class ReleaseReader(IServiceScopeFactory scopes)
{
    public async Task<ServerStatus> StatusAsync(Guid serverId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReleaseCatalog>().StatusAsync(serverId, ct);
    }

    public async Task<IReadOnlyList<ReleaseView>> ListAsync(Guid serverId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReleaseCatalog>().ListAsync(serverId, ct);
    }
}
