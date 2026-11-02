using CtxUi.Builder.Data;
using CtxUi.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Servers;

public sealed class ConnectedInboundsLoader(ConnectedInbounds connected, IServiceScopeFactory scopes) : IExtensionInitializer
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<BuilderDatabase>().Servers.AsNoTracking()
            .Where(server => server.InboundId != null).Select(server => server.InboundId!).ToListAsync(ct);
        connected.Replace(stored.Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty).Where(id => id != Guid.Empty));
    }
}
