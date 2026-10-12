using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Routing;

public sealed class RoutingSnapshotLoader(CtxUiDbContext db, ISecretVault vault, ILogger<RoutingSnapshotLoader> logger)
{
    public Task<long> CurrentRevisionAsync(CancellationToken ct = default) =>
        db.RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync(ct);

    public async Task<RoutingSnapshot> LoadAsync(CancellationToken ct = default)
    {
        // Ревизия читается раньше данных. Если данные изменятся между двумя чтениями,
        // снимок получит старую ревизию с новыми данными и на следующей проверке перечитается.
        var revision = await CurrentRevisionAsync(ct);

        var routes = new List<InboundRoute>();
        var inbounds = await db.Inbounds.AsNoTracking()
            .Where(inbound => inbound.Enabled && inbound.Kind == InboundKind.Http)
            .ToListAsync(ct);
        foreach (var inbound in inbounds)
        {
            var headers = UpstreamHeaders.Open(vault, inbound.UpstreamHeadersSealed);
            if (headers is null || inbound.Url is null)
            {
                logger.LogWarning(
                    "inbound {Slug} пропущен: нет адреса или не расшифрованы заголовки upstream, проверьте каталог keys",
                    inbound.Slug);
                continue;
            }

            routes.Add(new InboundRoute(
                inbound.Id,
                inbound.Slug,
                new Uri(inbound.Url),
                headers,
                inbound.ForwardIdentity,
                TimeSpan.FromSeconds(inbound.RequestTimeoutSeconds)));
        }

        var clients = await db.Clients.AsNoTracking().Include(client => client.Inbounds).ToListAsync(ct);
        return new RoutingSnapshot(revision, routes, clients.Select(client => KeyValuePair.Create(
            client.TokenHash,
            new ClientAccess(
                client.Id,
                client.Name,
                client.Enabled,
                client.ExpiresAt,
                client.Inbounds.Select(link => link.InboundId).ToFrozenSet(),
                client.Metadata))));
    }
}
