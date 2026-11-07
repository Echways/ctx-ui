using CtxUi.Builder.Data;
using CtxUi.Extensions;
using Microsoft.EntityFrameworkCore;

namespace CtxUi.Builder.Servers;

public sealed class ConnectedInboundsLoader(ConnectedInbounds connected, IDbContextFactory<BuilderDatabase> databases) : IExtensionInitializer
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var database = await databases.CreateDbContextAsync(ct);
        var stored = await database.Servers.AsNoTracking()
            .Where(server => server.InboundId != null).Select(server => server.InboundId!).ToListAsync(ct);
        connected.Replace(stored.Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty).Where(id => id != Guid.Empty));
    }
}
