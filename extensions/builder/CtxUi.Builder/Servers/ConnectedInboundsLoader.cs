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
            .Where(server => server.InboundId != null).Select(server => server.InboundId!.Value).ToListAsync(ct);
        connected.Replace(stored);
    }
}
